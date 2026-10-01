#!/bin/zsh
# Archive the iPhone app and upload it to App Store Connect for TestFlight.
# Signs with the Xcode account's automatic signing (team in project.yml);
# App Store Connect assigns the next build number (ExportOptions.plist).
# Then: App Store Connect → TestFlight; the build is ready after processing.
set -e
cd "$(dirname "$0")/.."
xcodegen generate -q
xcodebuild -project PerchRemote.xcodeproj -scheme PerchRemote -configuration Release \
  -destination 'generic/platform=iOS' -archivePath build/PerchRemote.xcarchive \
  -allowProvisioningUpdates archive | grep -E "error:|ARCHIVE"
xcodebuild -exportArchive -archivePath build/PerchRemote.xcarchive \
  -exportOptionsPlist ExportOptions.plist -exportPath build/export \
  -allowProvisioningUpdates 2>&1 | grep -E "error|EXPORT|Upload succeeded|UPLOAD"
