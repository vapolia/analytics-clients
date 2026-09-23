import XCTest
@testable import VapoliaAnalytics

final class CleanTests: XCTestCase {

    func testTextTrimsCapsAndStripsControlCharacters() {
        XCTAssertEqual(Clean.text("  hello  ", maxLength: 64), "hello")
        XCTAssertEqual(Clean.text("a\u{0}b", maxLength: 64), "ab")
        XCTAssertEqual(Clean.text(String(repeating: "x", count: 100), maxLength: 64)?.count, 64)
        XCTAssertNil(Clean.text("   ", maxLength: 64))
        XCTAssertNil(Clean.text(nil, maxLength: 64))
    }

    func testCountryIsTwoAsciiLettersOrNothing() {
        XCTAssertEqual(Clean.country("fr"), "FR")
        XCTAssertEqual(Clean.country(" FR "), "FR")
        // "GERMANY" truncated would be Georgia: unknown beats wrong.
        XCTAssertNil(Clean.country("GERMANY"))
        XCTAssertNil(Clean.country("F"))
        XCTAssertNil(Clean.country("F1"))
        XCTAssertNil(Clean.country(nil))
    }

    func testInstallIdIsACanonicalNonNilUuid() {
        XCTAssertEqual(
            Clean.installId("11111111-0000-0000-0000-000011111111"),
            "11111111-0000-0000-0000-000011111111"
        )
        XCTAssertEqual(
            Clean.installId("AABBCCDD-0000-0000-0000-00000000000F"),
            "aabbccdd-0000-0000-0000-00000000000f"
        )
        XCTAssertNil(Clean.installId("00000000-0000-0000-0000-000000000000"))
        XCTAssertNil(Clean.installId("not-a-uuid"))
        XCTAssertNil(Clean.installId("11111111000000000000000011111111"))
        XCTAssertNil(Clean.installId(nil))
    }

    func testPropsCapValues() {
        let kept = Clean.props([
            "mode": .string(String(repeating: "x", count: Limits.maxValueLength + 10)),
            "ok": true,
            "moves": 34,
            "nan": .number(.nan),
            "infinite": .number(.infinity),
            "blank": "   ",
        ])

        XCTAssertEqual(kept["ok"], .bool(true))
        XCTAssertEqual(kept["moves"], .number(34))
        if case .string(let mode)? = kept["mode"] {
            XCTAssertEqual(mode.count, Limits.maxValueLength)
        } else {
            XCTFail("mode should have survived, capped")
        }
        XCTAssertNil(kept["nan"])
        XCTAssertNil(kept["infinite"])
        XCTAssertNil(kept["blank"])
    }

    func testPropsAreCappedInCountInKeyOrder() {
        var props: [String: PropValue] = [:]
        for index in 0...20 {
            props[String(format: "p%02d", index)] = .number(Double(index))
        }

        let kept = Clean.props(props)

        XCTAssertEqual(kept.count, Limits.maxPropsPerEvent)
        XCTAssertEqual(kept["p00"], .number(0))
        XCTAssertNil(kept["p12"])
    }

    func testDeviceNormalizesAndRefusesExcludedCountries() throws {
        let clean = try XCTUnwrap(Device(country: "fr").cleaned())
        XCTAssertEqual(clean.country, "FR")

        XCTAssertNil(Device(country: "KR").cleaned(excluding: ["KR"]))
        XCTAssertNil(Device(country: "kr").cleaned(excluding: ["KR"]))
        XCTAssertEqual(Device(country: "KR").cleaned()?.country, "KR")
    }

    func testPropKeysAreCleanedLikeValues() {
        let kept = Clean.props([
            String(repeating: "k", count: Limits.maxValueLength + 10): .number(1),
            " mo\u{0}de ": .string("x"),
            "   ": .number(2),
        ])

        XCTAssertEqual(kept.count, 2)
        XCTAssertNotNil(kept[String(repeating: "k", count: Limits.maxValueLength)])
        XCTAssertEqual(kept["mode"], .string("x"))
    }
}
