// Hosted-only native Finder input helper. Never edits or prompts for TCC grants.
import AppKit
import ApplicationServices
import Carbon

let environment = ProcessInfo.processInfo.environment
guard environment["GITHUB_ACTIONS"] == "true",
      environment["RUNNER_ENVIRONMENT"] == "github-hosted" else {
    fputs("Requires a disposable GitHub-hosted macOS runner.\n", stderr)
    exit(1)
}
func fail(_ message: String) -> Never {
    fputs(message + "\n", stderr)
    exit(1)
}
func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
}
func bounds(_ element: AXUIElement) -> CGRect? {
    guard let p = attribute(element, kAXPositionAttribute), let s = attribute(element, kAXSizeAttribute),
          CFGetTypeID(p) == AXValueGetTypeID(), CFGetTypeID(s) == AXValueGetTypeID() else { return nil }
    var point = CGPoint.zero
    var size = CGSize.zero
    guard AXValueGetValue(p as! AXValue, .cgPoint, &point),
          AXValueGetValue(s as! AXValue, .cgSize, &size), size.width > 0, size.height > 0 else { return nil }
    return CGRect(origin: point, size: size)
}
let accessibility = AXIsProcessTrusted()
let postEvents = CGPreflightPostEventAccess()
let screenCapture = CGPreflightScreenCaptureAccess()
let finder = NSAppleEventDescriptor(bundleIdentifier: "com.apple.finder")
let automation = AEDeterminePermissionToAutomateTarget(finder.aeDesc, typeWildCard, typeWildCard, false)
let screens = NSScreen.screens.map { screen -> [String: Any] in
    let area = screen.visibleFrame
    return ["x": area.minX, "y": area.minY, "width": area.width, "height": area.height,
            "scale": screen.backingScaleFactor]
}
let ready = accessibility && postEvents && screenCapture && automation == noErr && !screens.isEmpty
let evidence: [String: Any] = [
    "accessibility": accessibility, "postEvents": postEvents, "screenCapture": screenCapture,
    "finderAutomationStatus": automation, "screens": screens,
    "inputPrerequisitesAvailable": ready, "finderDragVerified": false
]
let data = try JSONSerialization.data(withJSONObject: evidence, options: [.sortedKeys])
print("FINDER_INPUT_PREFLIGHT_JSON:" + String(decoding: data, as: UTF8.self))
if !ready {
    fail("Finder input prerequisites unavailable; no gesture attempted or permission state changed.")
}
let args = Array(CommandLine.arguments.dropFirst())
switch args.first ?? "--preflight" {
case "--preflight": break
case "--locate":
    guard args.count == 3,
          let process = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.finder").first else {
        fail("Expected Finder window title and fixture item name.")
    }
    let app = AXUIElementCreateApplication(process.processIdentifier)
    let windows = attribute(app, kAXWindowsAttribute) as? [AXUIElement] ?? []
    guard let window = windows.first(where: { attribute($0, kAXTitleAttribute) as? String == args[1] }),
          let frame = bounds(window) else { fail("Finder fixture window is not accessible.") }
    var queue = [window], index = 0
    while index < queue.count && index < 4000 {
        let node = queue[index]
        index += 1
        let name = attribute(node, kAXValueAttribute) as? String ?? attribute(node, kAXTitleAttribute) as? String
        if name == args[2], let rect = bounds(node), frame.contains(CGPoint(x: rect.midX, y: rect.midY)),
           rect.midY > frame.minY + 60 {
            let point = try JSONSerialization.data(withJSONObject: ["X": rect.midX, "Y": rect.midY])
            print("FINDER_POINT_JSON:" + String(decoding: point, as: UTF8.self))
            exit(0)
        }
        queue.append(contentsOf: attribute(node, kAXChildrenAttribute) as? [AXUIElement] ?? [])
    }
    fail("Finder did not expose the visible fixture label.")
case "--drag":
    let numbers = args.dropFirst().prefix(4).compactMap(Double.init)
    guard args.count == 6, numbers.count == 4, numbers.allSatisfy({ $0.isFinite }),
          NSScreen.screens.count == 1 else { fail("Expected four finite coordinates and one display.") }
    let from = CGPoint(x: numbers[0], y: numbers[1]), to = CGPoint(x: numbers[2], y: numbers[3])
    let desktop = CGDisplayBounds(CGMainDisplayID())
    let imagePath = URL(fileURLWithPath: args[5]).standardizedFileURL.path
    guard desktop.contains(from), desktop.contains(to),
          imagePath.hasPrefix(FileManager.default.currentDirectoryPath + "/artifacts/finder-drag/") else {
        fail("Coordinates or capture path escaped the isolated probe.")
    }
    let source = CGEventSource(stateID: .hidSystemState)
    func mouse(_ type: CGEventType, _ point: CGPoint, _ flags: CGEventFlags = []) {
        let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: .left)!
        event.flags = flags
        event.post(tap: .cghidEventTap)
    }
    func option(_ down: Bool) {
        let event = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_Option), keyDown: down)!
        event.flags = down ? .maskAlternate : []
        event.post(tap: .cghidEventTap)
    }
    defer { mouse(.leftMouseUp, to, .maskAlternate); option(false) }
    mouse(.mouseMoved, from)
    Thread.sleep(forTimeInterval: 0.15)
    mouse(.leftMouseDown, from)
    Thread.sleep(forTimeInterval: 0.15)
    mouse(.leftMouseDragged, CGPoint(x: from.x + 24, y: from.y + 6))
    Thread.sleep(forTimeInterval: 0.2)
    option(true) // Native macOS Copy modifier, after the drag threshold.
    for step in 1...16 {
        let fraction = Double(step) / 16
        mouse(.leftMouseDragged, CGPoint(x: from.x + (to.x - from.x) * fraction,
                                        y: from.y + (to.y - from.y) * fraction), .maskAlternate)
        Thread.sleep(forTimeInterval: 0.035)
    }
    Thread.sleep(forTimeInterval: 0.3)
    let capture = Process()
    capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    capture.arguments = ["-x", imagePath]
    try capture.run()
    capture.waitUntilExit()
    if capture.terminationStatus != 0 { throw NSError(domain: "FinderCapture", code: Int(capture.terminationStatus)) }
default: fail("Unknown Finder probe command.")
}
