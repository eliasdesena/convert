// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "Convert",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(name: "Convert", path: "Sources/Convert"),
        .testTarget(name: "ConvertTests", dependencies: ["Convert"])
    ]
)
