import SwiftUI
import Network
import UIKit
import ImageIO

private final class FrameProcessor {
    private var buffer = Data()
    private var expectedLength: Int?
    private let lock = NSLock()
    private let h264Decoder = H264FrameDecoder()

    func reset() {
        lock.lock()
        defer { lock.unlock() }

        buffer.removeAll(keepingCapacity: true)
        expectedLength = nil
        h264Decoder.reset()
    }

    func append(_ data: Data) -> (image: UIImage?, frameCount: Int, invalidLength: Bool) {
        lock.lock()
        defer { lock.unlock() }

        buffer.append(data)

        var latestImage: UIImage?
        var completedFrames = 0

        while true {
            if expectedLength == nil {
                guard buffer.count >= 4 else { break }

                let length = buffer.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
                buffer.removeFirst(4)

                guard length > 0 && length < 20_000_000 else {
                    buffer.removeAll(keepingCapacity: true)
                    expectedLength = nil
                    return (nil, completedFrames, true)
                }

                expectedLength = length
            }

            guard let length = expectedLength, buffer.count >= length else { break }

            let frameData = Data(buffer.prefix(length))
            buffer.removeFirst(length)
            expectedLength = nil
            completedFrames += 1

            if frameData.starts(with: [0xFF, 0xD8]) {
                if let source = CGImageSourceCreateWithData(frameData as CFData, nil),
                   let cgImage = CGImageSourceCreateImageAtIndex(
                       source,
                       0,
                       [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
                   ) {
                    latestImage = UIImage(cgImage: cgImage)
                }
            } else if let image = h264Decoder.decode(accessUnit: frameData) {
                latestImage = image
            }
        }

        guard let latestImage else {
            return (nil, completedFrames, false)
        }

        return (latestImage, completedFrames, false)
    }
}

@MainActor
final class FrameReceiver: ObservableObject {
    @Published var image: UIImage?
    @Published var status: String = "Starting…"
    @Published var frameCount: Int = 0

    private var listener: NWListener?
    private var connection: NWConnection?
    private let frameProcessor = FrameProcessor()
    private var pointerSendInFlight = false
    private var pendingPointerMove: Data?
    private var pendingPointerActions: [Data] = []

    func start(port: UInt16 = 5959) {
        guard listener == nil else { return }
        do {
           
            let p = NWEndpoint.Port(rawValue: port)!

            let tcpOptions = NWProtocolTCP.Options()
            tcpOptions.noDelay = true
            let parameters = NWParameters(tls: nil, tcp: tcpOptions)
            parameters.allowLocalEndpointReuse = true
            parameters.requiredLocalEndpoint = .hostPort(
                host: NWEndpoint.Host("127.0.0.1"),
                port: p
            )

            let l = try NWListener(using: parameters)
            l.newConnectionHandler = { [weak self] conn in
                Task { @MainActor in
                    self?.accept(conn)
                }
            }
            l.stateUpdateHandler = { [weak self] state in
                Task { @MainActor in
                    switch state {
                    case .ready:
                        self?.status = "Listening on \(port)"
                    case .failed(let error):
                        self?.status = "Listener failed: \(error)"
                    case .cancelled:
                        self?.status = "Listener stopped"
                    default:
                        break
                    }
                }
            }
            l.start(queue: DispatchQueue(label: "winpad.listener"))
            listener = l
        } catch {
            status = "Cannot start listener: \(error)"
        }
    }

    private func accept(_ conn: NWConnection) {
        connection?.cancel()
        connection = conn
        frameProcessor.reset()
        pointerSendInFlight = false
        pendingPointerMove = nil
        pendingPointerActions.removeAll(keepingCapacity: true)
        status = "Windows connected"

        conn.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                if case .failed(let error) = state {
                    self?.status = "Connection failed: \(error)"
                }
            }
        }
        conn.start(queue: DispatchQueue(label: "winpad.connection"))
        sendCapabilities(on: conn)
        receiveNext()
    }

    private func sendCapabilities(on connection: NWConnection) {
        connection.send(
            content: Data("V H264 JPEG\n".utf8),
            completion: .contentProcessed { _ in }
        )
    }

    private func receiveNext() {
        guard let connection else { return }
        let frameProcessor = frameProcessor

        connection.receive(minimumIncompleteLength: 1, maximumLength: 512 * 1024) { [weak self] data, _, isComplete, error in
            let result = data.flatMap { chunk in
                chunk.isEmpty ? nil : frameProcessor.append(chunk)
            }

            Task { @MainActor in
                guard let self else { return }

                if let result {
                    if result.invalidLength {
                        self.status = "Invalid frame length"
                        connection.cancel()
                        return
                    }

                    if let image = result.image {
                        self.image = image
                    }
                    self.frameCount += result.frameCount

                    if result.frameCount > 0 {
                        self.sendFrameAcknowledgement(on: connection)
                    }
                }

                if let error {
                    self.status = "Receive error: \(error)"
                    return
                }
                if isComplete {
                    self.status = "Windows disconnected"
                    return
                }
                self.receiveNext()
            }
        }
    }

    private func sendFrameAcknowledgement(on connection: NWConnection) {
        connection.send(
            content: Data([0x41, 0x0A]),
            completion: .contentProcessed { _ in }
        )
    }

    func sendPointer(_ action: String, x: CGFloat, y: CGFloat) {
        guard let connection else { return }

        let command = String(
            format: "%@ %.6f %.6f\n",
            locale: Locale(identifier: "en_US_POSIX"),
            action,
            Double(x),
            Double(y)
        )

        guard let data = command.data(using: .utf8) else { return }

        if pointerSendInFlight {
            if action == "M" {
                pendingPointerMove = data
            } else {
                // Pointer actions include their final coordinates, so an
                // older queued MOVE can be discarded before DOWN/UP/CLICK.
                pendingPointerMove = nil
                pendingPointerActions.append(data)
            }
            return
        }

        sendPointerData(data, on: connection)
    }

    private func sendPointerData(_ data: Data, on connection: NWConnection) {
        pointerSendInFlight = true

        connection.send(
            content: data,
            completion: .contentProcessed { [weak self] error in
                Task { @MainActor in
                    guard let self else { return }

                    if let error {
                        self.status = "Input send error: \(error)"
                        self.pointerSendInFlight = false
                        self.pendingPointerMove = nil
                        self.pendingPointerActions.removeAll()
                        return
                    }

                    self.sendNextPointerCommand(on: connection)
                }
            }
        )
    }

    private func sendNextPointerCommand(on connection: NWConnection) {
        if !pendingPointerActions.isEmpty {
            let data = pendingPointerActions.removeFirst()
            sendPointerData(data, on: connection)
        } else if let data = pendingPointerMove {
            pendingPointerMove = nil
            sendPointerData(data, on: connection)
        } else {
            pointerSendInFlight = false
        }
    }

    func stop() {
        connection?.cancel()
        listener?.cancel()
        connection = nil
        listener = nil
        pointerSendInFlight = false
        pendingPointerMove = nil
        pendingPointerActions.removeAll()
        status = "Stopped"
    }
}

struct ContentView: View {
    @StateObject private var receiver = FrameReceiver()
    @State private var touchStartedAt: Date?
    @State private var isDragging = false

    var body: some View {
        GeometryReader { geometry in
            ZStack {

                // 整个 iPad 屏幕作为黑色背景
                Color.black
                    .ignoresSafeArea()

                if let image = receiver.image {

                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                        .frame(
                            width: geometry.size.width,
                            height: geometry.size.height
                        )
                        .clipped()
                        .ignoresSafeArea()

                } else {

                    VStack(spacing: 14) {
                        ProgressView()
                            .tint(.white)

                        Text("Waiting for Windows…")
                            .foregroundStyle(.white)
                    }
                    .frame(
                        width: geometry.size.width,
                        height: geometry.size.height
                    )
                }

            }
            .frame(
                width: geometry.size.width,
                height: geometry.size.height
            )
            .ignoresSafeArea()
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0, coordinateSpace: .local)
                    .onChanged { value in
                        guard let image = receiver.image else { return }

                        let point = normalizedPoint(
                            value.location,
                            viewSize: geometry.size,
                            imageSize: image.size
                        )

                        if touchStartedAt == nil {
                            touchStartedAt = Date()
                        }

                        if !isDragging,
                           let startedAt = touchStartedAt,
                           Date().timeIntervalSince(startedAt) >= 0.35 {
                            isDragging = true
                            receiver.sendPointer("D", x: point.x, y: point.y)
                        }

                        receiver.sendPointer("M", x: point.x, y: point.y)
                    }
                    .onEnded { value in
                        guard let image = receiver.image else {
                            resetTouchState()
                            return
                        }

                        let point = normalizedPoint(
                            value.location,
                            viewSize: geometry.size,
                            imageSize: image.size
                        )

                        receiver.sendPointer(
                            isDragging ? "U" : "C",
                            x: point.x,
                            y: point.y
                        )
                        resetTouchState()
                    }
            )
            .overlay(alignment: .topLeading) {
                // 左上角状态信息覆盖在画面上，
                // 不参与图像布局
                VStack(alignment: .leading, spacing: 4) {

                    Text("WinPad Client")
                        .font(.headline)

                    Text(receiver.status)

                    Text("Frames: \(receiver.frameCount)")

                    Text("View: \(Int(geometry.size.width))x\(Int(geometry.size.height))")

                    Text("Screen: \(Int(UIScreen.main.bounds.width))x\(Int(UIScreen.main.bounds.height))")

                    if let image = receiver.image {
                        Text("Image: \(Int(image.size.width))x\(Int(image.size.height))")
                    }

                    Text(
                        "Safe: L\(Int(geometry.safeAreaInsets.leading)) " +
                        "T\(Int(geometry.safeAreaInsets.top)) " +
                        "R\(Int(geometry.safeAreaInsets.trailing)) " +
                        "B\(Int(geometry.safeAreaInsets.bottom))"
                    )
                }
                .font(.caption.monospaced())
                .foregroundStyle(.white)
                .padding(10)
                .background(.black.opacity(0.55))
                .clipShape(
                    RoundedRectangle(cornerRadius: 8)
                )
                .padding()
                .allowsHitTesting(false)
            }
        }
        .ignoresSafeArea()

        .onAppear {
            // 使用 WinPad 时禁止 iPad 自动锁屏
            UIApplication.shared.isIdleTimerDisabled = true

            receiver.start()
        }

        .onDisappear {
            UIApplication.shared.isIdleTimerDisabled = false

            receiver.stop()
        }

        // 隐藏 iPad 顶部/底部系统覆盖层
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
    }

    private func normalizedPoint(
        _ location: CGPoint,
        viewSize: CGSize,
        imageSize: CGSize
    ) -> CGPoint {
        guard viewSize.width > 0,
              viewSize.height > 0,
              imageSize.width > 0,
              imageSize.height > 0 else {
            return .zero
        }

        let scale = max(
            viewSize.width / imageSize.width,
            viewSize.height / imageSize.height
        )
        let displayedWidth = imageSize.width * scale
        let displayedHeight = imageSize.height * scale
        let offsetX = (viewSize.width - displayedWidth) / 2
        let offsetY = (viewSize.height - displayedHeight) / 2

        return CGPoint(
            x: min(max((location.x - offsetX) / displayedWidth, 0), 1),
            y: min(max((location.y - offsetY) / displayedHeight, 0), 1)
        )
    }

    private func resetTouchState() {
        touchStartedAt = nil
        isDragging = false
    }
}
