// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "ClipShelf",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "ClipShelf", targets: ["ClipShelfLite"])
    ],
    targets: [
        .executableTarget(
            name: "ClipShelfLite",
            path: "Sources/ClipShelfLite"
        ),
        .testTarget(
            name: "ClipShelfLiteTests",
            dependencies: ["ClipShelfLite"],
            path: "MacTests/ClipShelfLiteTests"
        )
    ]
)
