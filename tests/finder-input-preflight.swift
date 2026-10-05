// Read-only prerequisites for a hosted Finder drag test; never edits or prompts for TCC grants.
import AppKit
import ApplicationServices
import Carbon

let environment = ProcessInfo.processInfo.environment
guard environment["GITHUB_ACTIONS"] == "true",
      environment["RUNNER_ENVIRONMENT"] == "github-hosted" else {
    fputs("Requires a disposable GitHub-hosted macOS runner.\n", stderr)
    exit(1)
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
    fputs("Finder input prerequisites unavailable; no gesture attempted or permission state changed.\n", stderr)
    exit(1)
}
