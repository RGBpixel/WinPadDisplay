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

            let parameters = NWParameters.tcp
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
}
