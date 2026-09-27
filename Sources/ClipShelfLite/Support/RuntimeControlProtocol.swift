import Darwin
import Foundation

enum RuntimeControlError: LocalizedError {
    case invalidSocketPath(String)
    case socketOperation(String)
    case invalidResponse(String)

    var errorDescription: String? {
        switch self {
        case .invalidSocketPath(let message),
             .socketOperation(let message),
             .invalidResponse(let message):
            return message
        }
    }
}

struct RuntimeControlRequest: Codable {
    let command: String
    let arguments: [String]
}

struct RuntimeControlResponse: Codable {
    let ok: Bool
    let command: String
    var error: String?
    var alive: Bool?
    var version: String?
    var itemCount: Int?
    var maxItems: Int?
    var isRecording: Bool?
    var dataDirectory: String?
    var exportedPath: String?
    var injected: Bool?
    var cleared: Bool?
    var quitting: Bool?

    static func failure(command: String, error: String) -> RuntimeControlResponse {
        RuntimeControlResponse(ok: false, command: command, error: error)
    }
}

enum RuntimeControlJSON {
    static func encode<T: Encodable>(_ value: T) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(value)
    }

    static func printResponse(_ response: RuntimeControlResponse) {
        if let data = try? encode(response), let line = String(data: data, encoding: .utf8) {
            print(line)
        } else {
            print("{\"ok\":false,\"error\":\"response encoding failed\"}")
        }
    }
}

enum UnixSocketTransport {
    static let maximumMessageSize = 1_048_576

    static func makeSocket() throws -> Int32 {
        let descriptor = socket(AF_UNIX, SOCK_STREAM, 0)
        guard descriptor >= 0 else {
            throw posixError("socket")
        }
        return descriptor
    }

    static func bind(_ descriptor: Int32, to path: String) throws {
        var address = try socketAddress(path: path)
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                Darwin.bind(
                    descriptor,
                    sockaddrPointer,
                    socklen_t(MemoryLayout<sockaddr_un>.size)
                )
            }
        }
        guard result == 0 else {
            throw posixError("bind")
        }
    }

    static func connect(_ descriptor: Int32, to path: String) throws {
        var address = try socketAddress(path: path)
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                Darwin.connect(
                    descriptor,
                    sockaddrPointer,
                    socklen_t(MemoryLayout<sockaddr_un>.size)
                )
            }
        }
        guard result == 0 else {
            throw posixError("connect")
        }
    }

    static func write(_ data: Data, to descriptor: Int32) throws {
        try data.withUnsafeBytes { rawBuffer in
            guard let baseAddress = rawBuffer.baseAddress else { return }
            var sent = 0
            while sent < rawBuffer.count {
                let result = Darwin.write(
                    descriptor,
                    baseAddress.advanced(by: sent),
                    rawBuffer.count - sent
                )
                if result < 0, errno == EINTR {
                    continue
                }
                guard result > 0 else {
                    throw posixError("write")
                }
                sent += result
            }
        }
    }

    static func read(from descriptor: Int32) throws -> Data {
        var result = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)

        while true {
            let count = Darwin.read(descriptor, &buffer, buffer.count)
            if count < 0, errno == EINTR {
                continue
            }
            guard count >= 0 else {
                throw posixError("read")
            }
            if count == 0 {
                return result
            }
            result.append(buffer, count: count)
            guard result.count <= maximumMessageSize else {
                throw RuntimeControlError.invalidResponse("control message is too large")
            }
        }
    }

    static func posixError(_ operation: String) -> RuntimeControlError {
        let message = String(cString: strerror(errno))
        return .socketOperation("\(operation) failed: \(message)")
    }

    private static func socketAddress(path: String) throws -> sockaddr_un {
        let pathBytes = Array(path.utf8CString)
        var address = sockaddr_un()
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard pathBytes.count <= capacity else {
            throw RuntimeControlError.invalidSocketPath(
                "Unix socket path is too long (maximum \(capacity - 1) UTF-8 bytes)"
            )
        }

        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        address.sun_family = sa_family_t(AF_UNIX)
        _ = pathBytes.withUnsafeBytes { source in
            withUnsafeMutablePointer(to: &address.sun_path.0) { destination in
                memcpy(destination, source.baseAddress, pathBytes.count)
            }
        }
        return address
    }
}

enum RuntimeControlClient {
    static func send(
        _ request: RuntimeControlRequest,
        socketURL: URL
    ) throws -> RuntimeControlResponse {
        let descriptor = try UnixSocketTransport.makeSocket()
        defer { Darwin.close(descriptor) }

        try UnixSocketTransport.connect(descriptor, to: socketURL.path)
        try UnixSocketTransport.write(RuntimeControlJSON.encode(request), to: descriptor)
        Darwin.shutdown(descriptor, SHUT_WR)
        let responseData = try UnixSocketTransport.read(from: descriptor)
        guard !responseData.isEmpty else {
            throw RuntimeControlError.invalidResponse("control server returned an empty response")
        }
        do {
            return try JSONDecoder().decode(RuntimeControlResponse.self, from: responseData)
        } catch {
            throw RuntimeControlError.invalidResponse(
                "control server returned invalid JSON: \(error.localizedDescription)"
            )
        }
    }
}

enum RuntimeControlCommand {
    static func runIfRequested(
        arguments: [String] = CommandLine.arguments,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> Int32? {
        guard arguments.dropFirst().first == "--ctl" else {
            return nil
        }

        let outcome = execute(arguments: arguments, environment: environment)
        RuntimeControlJSON.printResponse(outcome.response)
        return outcome.exitCode
    }

    static func execute(
        arguments: [String],
        environment: [String: String]
    ) -> (exitCode: Int32, response: RuntimeControlResponse) {

        let commandName = arguments.count > 2 ? arguments[2] : "unknown"
        do {
            let request = try request(from: Array(arguments.dropFirst(2)))
            guard let socketURL = try AppEnvironment.controlSocketURL(environment: environment) else {
                throw RuntimeControlError.invalidSocketPath(
                    "CLIPSHELF_CONTROL_SOCKET is not set"
                )
            }
            let response = try RuntimeControlClient.send(request, socketURL: socketURL)
            return (response.ok ? 0 : 1, response)
        } catch {
            let response = RuntimeControlResponse.failure(
                command: commandName,
                error: error.localizedDescription
            )
            return (2, response)
        }
    }

    static func request(from arguments: [String]) throws -> RuntimeControlRequest {
        guard let command = arguments.first else {
            throw RuntimeControlError.invalidResponse(usage)
        }
        let remaining = Array(arguments.dropFirst())

        switch command {
        case "ping", "status", "clear", "quit":
            guard remaining.isEmpty else {
                throw RuntimeControlError.invalidResponse(usage)
            }
        case "export", "set-limit":
            guard remaining.count == 1 else {
                throw RuntimeControlError.invalidResponse(usage)
            }
        case "inject-text":
            guard !remaining.isEmpty else {
                throw RuntimeControlError.invalidResponse(usage)
            }
            if remaining.count > 1 {
                guard remaining.count == 3, remaining[1] == "--type" else {
                    throw RuntimeControlError.invalidResponse(usage)
                }
            }
        default:
            throw RuntimeControlError.invalidResponse("Unknown control command: \(command). \(usage)")
        }
        return RuntimeControlRequest(command: command, arguments: remaining)
    }

    static let usage = "Usage: ClipShelf --ctl ping|status|export <path>|inject-text <text> [--type <pasteboard-type>]|set-limit <number>|clear|quit"
}
