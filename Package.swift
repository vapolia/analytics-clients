// swift-tools-version:5.9
//
// The Swift client for the collector. The manifest sits at the repository root because SwiftPM
// requires it there; the sources live in clients/swift, next to the other clients.
//
//   .package(url: "https://github.com/vapolia/analytics-clients", from: "1.0.0")

import PackageDescription

let package = Package(
    name: "VapoliaAnalytics",
    platforms: [
        .iOS(.v15),
        .macCatalyst(.v15),
        // Not a shipping platform: it is what `swift build` and `swift test` compile for on a Mac.
        // v12 is what `URLSession.data(for:)` needs in Transport.
        .macOS(.v12),
    ],
    products: [
        .library(name: "VapoliaAnalytics", targets: ["VapoliaAnalytics"]),
    ],
    targets: [
        .target(
            name: "VapoliaAnalytics",
            path: "clients/swift/Sources/VapoliaAnalytics",
            resources: [.copy("PrivacyInfo.xcprivacy")]
        ),
        .testTarget(
            name: "VapoliaAnalyticsTests",
            dependencies: ["VapoliaAnalytics"],
            path: "clients/swift/Tests/VapoliaAnalyticsTests"
        ),
    ]
)
