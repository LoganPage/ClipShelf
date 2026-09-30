import AppKit
import Darwin
import Foundation

final class RuntimeControlServer {
    private let socketURL: URL
    private unowned let store: ClipStore
    private let acceptQueue = DispatchQueue(label: "ClipShelf.RuntimeControl.Accept")
    private let clientQueue = DispatchQueue(
        label: "ClipShelf.RuntimeControl.Client",
        attributes: .concurrent
    )
    private let stateLock = NSLock()
    private var listeningDescriptor: Int32 = -1
    private var isRunning = false

    private init(socketURL: URL, store: ClipStore) {
        self.socketURL = socketURL
        self.store = store
    }

    static func startIfConfigured(
        store: ClipStore,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        fileManager: FileManager = .default
    ) throws -> RuntimeControlServer? {
        guard let socketURL = try AppEnvironment.controlSocketURL(
            environment: environment,
            fileManager: fileManager
        ) else {
            return nil
        }

        let server = RuntimeControlServer(socketURL: socketURL, store: store)
        try server.start(fileManager: fileManager)
        return server
    }

    func stop() {
        stateLock.lock()
        guard isRunning else {
            stateLock.unlock()
            return
        }
        isRunning = false
        let descriptor = listeningDescriptor
        listeningDescriptor = -1
        stateLock.unlock()

        if descriptor >= 0 {
            Darwin.shutdown(descriptor, SHUT_RDWR)
            Darwin.close(descriptor)
        }
        unlink(socketURL.path)
    }

    private func start(fileManager: FileManager) throws {
        try fileManager.createDirectory(
            at: socketURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try ensureSocketPathIsAvailable(fileManager: fileManager)

        let descriptor = try UnixSocketTransport.makeSocket()
        do {
            try UnixSocketTransport.bind(descriptor, to: socketURL.path)
            guard chmod(socketURL.path, S_IRUSR | S_IWUSR) == 0 else {
                throw UnixSocketTransport.posixError("chmod")
            }
            guard Darwin.listen(descriptor, 8) == 0 else {
                throw UnixSocketTransport.posixError("listen")
            }
        } catch {
            Darwin.close(descriptor)
            unlink(socketURL.path)
            throw error
        }

        stateLock.lock()
        listeningDescriptor = descriptor
        isRunning = true
        stateLock.unlock()
        acceptQueue.async { [weak self] in
            self?.acceptConnections()
        }
    }

    private func acceptConnections() {
        while currentRunningState().running {
            let state = currentRunningState()
            guard state.descriptor >= 0 else { return }
            let clientDescriptor = Darwin.accept(state.descriptor, nil, nil)
            if clientDescriptor < 0 {
                if errno == EINTR { continue }
                if !currentRunningState().running { return }
                continue
            }
            clientQueue.async { [weak self] in
                self?.handleClient(clientDescriptor)
            }
        }
    }

    private func handleClient(_ descriptor: Int32) {
        defer { Darwin.close(descriptor) }
        let response: RuntimeControlResponse
        do {
            let requestData = try UnixSocketTransport.read(from: descriptor)
            let request = try JSONDecoder().decode(RuntimeControlRequest.self, from: requestData)
            response = try handle(request)
        } catch {
            response = .failure(command: "unknown", error: error.localizedDescription)
        }

        do {
            try UnixSocketTransport.write(RuntimeControlJSON.encode(response), to: descriptor)
        } catch {
            NSLog("ClipShelf control response failed: \(error.localizedDescription)")
        }

        if response.quitting == true {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                NSApp.terminate(nil)
            }
        }
    }

    private func handle(_ request: RuntimeControlRequest) throws -> RuntimeControlResponse {
        switch request.command {
        case "ping":
            return RuntimeControlResponse(
                ok: true,
                command: request.command,
                alive: true,
                version: appVersion
            )
        case "status":
            return onMain {
                RuntimeControlResponse(
                    ok: true,
                    command: request.command,
                    itemCount: store.items.count,
                    maxItems: store.maxItems,
                    isRecording: store.isClipboardHistoryEnabled,
                    dataDirectory: store.storageURL.deletingLastPathComponent().path
                )
            }
        case "export":
            guard let path = request.arguments.first else {
                return .failure(command: request.command, error: "export requires a path")
            }
            let items = onMain { store.items }
            let exportURL = URL(
                fileURLWithPath: NSString(string: path).expandingTildeInPath,
                relativeTo: URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
            ).standardizedFileURL
            try FileManager.default.createDirectory(
                at: exportURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(items).write(to: exportURL, options: .atomic)
            return RuntimeControlResponse(
                ok: true,
                command: request.command,
                itemCount: items.count,
                exportedPath: exportURL.path
            )
        case "inject-text":
            guard let text = request.arguments.first else {
                return .failure(command: request.command, error: "inject-text requires text")
            }
            let additionalType = request.arguments.count == 3 ? request.arguments[2] : nil
            let injected = onMain {
                store.injectControlText(text, additionalTypeName: additionalType)
            }
            return RuntimeControlResponse(
                ok: injected,
                command: request.command,
                error: injected ? nil : "could not write to the configured pasteboard",
                injected: injected
            )
        case "set-limit":
            guard let rawValue = request.arguments.first, let value = Int(rawValue) else {
                return .failure(command: request.command, error: "set-limit requires an integer")
            }
            return onMain {
                store.setHistoryLimit(value)
                return RuntimeControlResponse(
                    ok: true,
                    command: request.command,
                    itemCount: store.items.count,
                    maxItems: store.maxItems
                )
            }
        case "clear":
            return onMain {
                store.clearHistory()
                return RuntimeControlResponse(
                    ok: true,
                    command: request.command,
                    itemCount: store.items.count,
                    cleared: true
                )
            }
        case "quit":
            return RuntimeControlResponse(
                ok: true,
                command: request.command,
                quitting: true
            )
        default:
            return .failure(
                command: request.command,
                error: "unknown control command: \(request.command)"
            )
        }
    }

    private var appVersion: String {
        AppVersionInfo.statusVersion(bundleVersion: AppVersionInfo.bundleVersion)
    }

    private func onMain<T>(_ operation: () -> T) -> T {
        if Thread.isMainThread {
            return operation()
        }
        return DispatchQueue.main.sync(execute: operation)
    }

    private func currentRunningState() -> (running: Bool, descriptor: Int32) {
        stateLock.lock()
        defer { stateLock.unlock() }
        return (isRunning, listeningDescriptor)
    }

    private func ensureSocketPathIsAvailable(fileManager: FileManager) throws {
        guard fileManager.fileExists(atPath: socketURL.path) else { return }
        var info = stat()
        guard lstat(socketURL.path, &info) == 0 else {
            throw UnixSocketTransport.posixError("lstat")
        }
        let kind = info.st_mode & S_IFMT == S_IFSOCK ? "socket" : "non-socket file"
        throw RuntimeControlError.invalidSocketPath(
            "Refusing to replace existing \(kind) at \(socketURL.path)"
        )
    }
}
