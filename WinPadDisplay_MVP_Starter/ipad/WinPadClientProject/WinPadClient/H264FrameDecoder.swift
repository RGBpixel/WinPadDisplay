import CoreImage
import CoreMedia
import Foundation
import UIKit
import VideoToolbox

/// Decodes one Annex-B H.264 access unit at a time.
/// The Windows sender must include SPS and PPS before the first IDR frame
/// and repeat them whenever the encoder format changes.
final class H264FrameDecoder {
    private var session: VTDecompressionSession?
    private var formatDescription: CMVideoFormatDescription?
    private var sequenceParameterSet: Data?
    private var pictureParameterSet: Data?
    private let imageContext = CIContext(options: [.cacheIntermediates: false])

    deinit {
        reset()
    }

    func reset() {
        if let session {
            VTDecompressionSessionInvalidate(session)
        }

        session = nil
        formatDescription = nil
        sequenceParameterSet = nil
        pictureParameterSet = nil
    }

    func decode(accessUnit: Data) -> UIImage? {
        let nalUnits = Self.splitAnnexB(accessUnit)
        guard !nalUnits.isEmpty else { return nil }

        var parameterSetsChanged = false
        for nalUnit in nalUnits where !nalUnit.isEmpty {
            switch nalUnit[0] & 0x1F {
            case 7:
                if sequenceParameterSet != nalUnit {
                    sequenceParameterSet = nalUnit
                    parameterSetsChanged = true
                }
            case 8:
                if pictureParameterSet != nalUnit {
                    pictureParameterSet = nalUnit
                    parameterSetsChanged = true
                }
            default:
                break
            }
        }

        if parameterSetsChanged || session == nil {
            guard rebuildSessionIfPossible() else { return nil }
        }

        let pictureNALUnits = nalUnits.filter { nalUnit in
            guard let firstByte = nalUnit.first else { return false }
            let type = firstByte & 0x1F
            return type != 7 && type != 8 && type != 9
        }

        guard !pictureNALUnits.isEmpty,
              let session,
              let formatDescription else {
            return nil
        }

        var avccData = Data()
        avccData.reserveCapacity(
            pictureNALUnits.reduce(0) { $0 + 4 + $1.count }
        )

        for nalUnit in pictureNALUnits {
            var length = UInt32(nalUnit.count).bigEndian
            withUnsafeBytes(of: &length) { avccData.append(contentsOf: $0) }
            avccData.append(nalUnit)
        }

        guard let sampleBuffer = makeSampleBuffer(
            avccData: avccData,
            formatDescription: formatDescription
        ) else {
            return nil
        }

        var decodedImage: UIImage?
        let status = VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: sampleBuffer,
            flags: [],
            infoFlagsOut: nil
        ) { [imageContext] decodeStatus, _, imageBuffer, _, _ in
            guard decodeStatus == noErr, let imageBuffer else { return }

            let ciImage = CIImage(cvPixelBuffer: imageBuffer)
            guard let cgImage = imageContext.createCGImage(
                ciImage,
                from: ciImage.extent
            ) else {
                return
            }

            decodedImage = UIImage(cgImage: cgImage)
        }

        guard status == noErr else { return nil }
        return decodedImage
    }

    private func rebuildSessionIfPossible() -> Bool {
        guard let sequenceParameterSet,
              let pictureParameterSet else {
            return false
        }

        var newFormatDescription: CMVideoFormatDescription?
        let formatStatus = sequenceParameterSet.withUnsafeBytes { spsBytes in
            pictureParameterSet.withUnsafeBytes { ppsBytes in
                guard let spsAddress = spsBytes.baseAddress?
                          .assumingMemoryBound(to: UInt8.self),
                      let ppsAddress = ppsBytes.baseAddress?
                          .assumingMemoryBound(to: UInt8.self) else {
                    return kCMFormatDescriptionError_InvalidParameter
                }

                let pointers = [spsAddress, ppsAddress]
                let sizes = [sequenceParameterSet.count, pictureParameterSet.count]

                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: pointers.count,
                    parameterSetPointers: pointers,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &newFormatDescription
                )
            }
        }

        guard formatStatus == noErr, let newFormatDescription else {
            return false
        }

        if let session {
            VTDecompressionSessionInvalidate(session)
        }

        let imageAttributes: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary
        ]

        var newSession: VTDecompressionSession?
        let sessionStatus = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: newFormatDescription,
            decoderSpecification: nil,
            imageBufferAttributes: imageAttributes as CFDictionary,
            outputCallback: nil,
            decompressionSessionOut: &newSession
        )

        guard sessionStatus == noErr, let newSession else {
            return false
        }

        formatDescription = newFormatDescription
        session = newSession
        return true
    }

    private func makeSampleBuffer(
        avccData: Data,
        formatDescription: CMVideoFormatDescription
    ) -> CMSampleBuffer? {
        var blockBuffer: CMBlockBuffer?
        let blockStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: avccData.count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: avccData.count,
            flags: 0,
            blockBufferOut: &blockBuffer
        )

        guard blockStatus == kCMBlockBufferNoErr,
              let blockBuffer else {
            return nil
        }

        let copyStatus = avccData.withUnsafeBytes { bytes in
            guard let source = bytes.baseAddress else {
                return kCMBlockBufferBadPointerParameterErr
            }

            return CMBlockBufferReplaceDataBytes(
                with: source,
                blockBuffer: blockBuffer,
                offsetIntoDestination: 0,
                dataLength: avccData.count
            )
        }

        guard copyStatus == kCMBlockBufferNoErr else { return nil }

        var sampleSize = avccData.count
        var sampleBuffer: CMSampleBuffer?
        let sampleStatus = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 0,
            sampleTimingArray: nil,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )

        guard sampleStatus == noErr else { return nil }
        return sampleBuffer
    }

    private static func splitAnnexB(_ data: Data) -> [Data] {
        let bytes = [UInt8](data)
        var starts: [(offset: Int, length: Int)] = []
        var index = 0

        while index + 3 <= bytes.count {
            if index + 4 <= bytes.count,
               bytes[index] == 0,
               bytes[index + 1] == 0,
               bytes[index + 2] == 0,
               bytes[index + 3] == 1 {
                starts.append((index, 4))
                index += 4
            } else if bytes[index] == 0,
                      bytes[index + 1] == 0,
                      bytes[index + 2] == 1 {
                starts.append((index, 3))
                index += 3
            } else {
                index += 1
            }
        }

        guard !starts.isEmpty else { return [] }

        return starts.enumerated().compactMap { item in
            let payloadStart = item.element.offset + item.element.length
            let payloadEnd = item.offset + 1 < starts.count
                ? starts[item.offset + 1].offset
                : bytes.count

            guard payloadStart < payloadEnd else { return nil }
            return Data(bytes[payloadStart..<payloadEnd])
        }
    }
}
