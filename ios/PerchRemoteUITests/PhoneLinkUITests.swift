// End to end against a real Perch: pair by opening the perch:// link, open a
// Claude tab, type a line, and wait for Claude's answer to come back.
//
// Needs a Perch with the phone link on and a Claude tab. Pass the pairing
// link and the tab's title through xcodebuild:
//   TEST_RUNNER_PERCH_PAIR_URL='perch://pair?...' \
//   TEST_RUNNER_PERCH_TAB='phone test' xcodebuild test ...
// Skipped when PERCH_PAIR_URL is not set.

import XCTest

final class PhoneLinkUITests: XCTestCase {
    override func setUp() { continueAfterFailure = false }

    func testPairSendAndHearBack() throws {
        let env = ProcessInfo.processInfo.environment
        guard let link = env["PERCH_PAIR_URL"].flatMap(URL.init(string:)) else {
            throw XCTSkip("PERCH_PAIR_URL not set")
        }
        let tab = env["PERCH_TAB"] ?? "phone test"
        let word = "PONG\(Int.random(in: 1000...9999))"

        let app = XCUIApplication()
        app.launchArguments = ["-PerchForgetPairings"]
        app.launch()
        XCTAssertTrue(app.staticTexts["Connect to Perch"].waitForExistence(timeout: 5), "a first launch asks to pair")
        shot("1-first-launch")

        // The way a scanned code arrives: the system opens the link, asks.
        app.open(link)
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        let openButton = springboard.buttons["Open"]
        if openButton.waitForExistence(timeout: 5) { openButton.tap() }

        let row = app.buttons.containing(NSPredicate(format: "label CONTAINS %@", tab)).firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 20), "the Claude tab shows up in the list")
        shot("2-sessions")
        row.tap()

        let field = app.descendants(matching: .any)["typeField"].firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 10))
        field.tap()
        field.typeText("Reply with exactly this word and nothing else: \(word)")
        app.buttons["sendButton"].tap()
        shot("3-sent")

        let reply = app.descendants(matching: .any)["replyText"].firstMatch
        let answered = NSPredicate(format: "label CONTAINS %@", word)
        let got = expectation(for: answered, evaluatedWith: reply)
        wait(for: [got], timeout: 180)
        shot("4-answered")
    }

    /// A new session from the phone: a Claude tab opens in the project on the
    /// computer with the phone's first message, and its answer comes back.
    func testNewSessionFromThePhone() throws {
        let env = ProcessInfo.processInfo.environment
        guard let link = env["PERCH_PAIR_URL"].flatMap(URL.init(string:)) else {
            throw XCTSkip("PERCH_PAIR_URL not set")
        }
        let word = "NEWTAB\(Int.random(in: 1000...9999))"
        let app = XCUIApplication()
        app.launchArguments = ["-PerchForgetPairings"]
        app.launchEnvironment["PERCH_PAIR_URL"] = link.absoluteString
        app.launch()

        let new = app.buttons["New session"]
        XCTAssertTrue(new.waitForExistence(timeout: 20))
        XCTAssertTrue(new.waitForEnabled(timeout: 10))
        new.tap()
        let first = app.textFields["What should Claude do?"].firstMatch
        XCTAssertTrue(first.waitForExistence(timeout: 10))
        first.tap()
        first.typeText("Reply with exactly this word and nothing else: \(word)")
        shot("5-new-session")
        let start = app.buttons["Start"]
        XCTAssertTrue(start.waitForEnabled(timeout: 10), "the only project is picked")
        start.tap()

        let reply = app.descendants(matching: .any)["replyText"].firstMatch
        let got = expectation(for: NSPredicate(format: "label CONTAINS %@", word), evaluatedWith: reply)
        wait(for: [got], timeout: 180)
        shot("6-new-session-answered")
    }

    private func shot(_ name: String) {
        let a = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        a.name = name
        a.lifetime = .keepAlways
        add(a)
    }
}

extension XCUIElement {
    func waitForEnabled(timeout: TimeInterval) -> Bool {
        let done = XCTNSPredicateExpectation(predicate: NSPredicate(format: "isEnabled == true"), object: self)
        return XCTWaiter.wait(for: [done], timeout: timeout) == .completed
    }
}
