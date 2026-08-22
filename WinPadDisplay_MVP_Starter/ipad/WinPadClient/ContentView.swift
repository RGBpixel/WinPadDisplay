import SwiftUI
import Network
import UIKit

@MainActor
final class FrameReceiver: ObservableObject {
    @Published var image: UIImage?
    @Published var status: String = "Starting…"
    @Published var frameCount: Int = 0

    private var listener: NWListener?
    private var connection: NWConnection?
    private var buffer = Data()
    private var expectedLength: Int?

    func start(port: UInt16 = 5959) {
        guard listener == nil else { return }
        do {
            let p = NWEndpoint.Port(rawValue: port)!
            let l = try NWListener(using: .tcp, on: p)
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
        buffer.removeAll(keepingCapacity: true)
        expectedLength = nil
        status = "Windows connected"

        conn.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                if case .failed(let error) = state {
                    self?.status = "Connection failed: \(error)"
                }
            }
        }
        conn.start(queue: DispatchQueue(label: "winpad.connection"))
        receiveNext()
    }

    private func receiveNext() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 512 * 1024) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            Task { @MainActor in
                if let data, !data.isEmpty {
                    self.buffer.append(data)
                    self.consumeFrames()
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

    private func consumeFrames() {
        while true {
            if expectedLength == nil {
                guard buffer.count >= 4 else { return }
                let length = buffer.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
                buffer.removeFirst(4)
                guard length > 0 && length < 20_000_000 else {
                    status = "Invalid frame length"
                    connection?.cancel()
                    return
                }
                expectedLength = length
            }

            guard let length = expectedLength, buffer.count >= length else { return }
            let jpg = buffer.prefix(length)
            buffer.removeFirst(length)
            expectedLength = nil

            if let decoded = UIImage(data: Data(jpg)) {
                image = decoded
                frameCount += 1
            }
        }
    }

    func stop() {
        connection?.cancel()
        listener?.cancel()
        connection = nil
        listener = nil
        status = "Stopped"
    }
}

struct ContentView: View {
    @StateObject private var receiver = FrameReceiver()

    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.black.ignoresSafeArea()

            if let image = receiver.image {
                Image(uiImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                VStack(spacing: 14) {
                    ProgressView()
                    Text("Waiting for Windows…")
                        .foregroundStyle(.white)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("WinPad Client")
                    .font(.headline)
                Text(receiver.status)
                Text("Frames: \(receiver.frameCount)")
            }
            .font(.caption.monospaced())
            .foregroundStyle(.white)
            .padding(10)
            .background(.black.opacity(0.55))
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .padding()
        }
        .onAppear { receiver.start() }
        .onDisappear { receiver.stop() }
        .persistentSystemOverlays(.hidden)
    }
}
