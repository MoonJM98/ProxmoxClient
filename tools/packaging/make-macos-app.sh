#!/usr/bin/env bash
# macOS 앱(.app) 만들기 — Linux·Windows·macOS 어디서나 실행 가능(크로스 게시).
#   사용: tools/packaging/make-macos-app.sh [arm64|x64] [출력 폴더]
# 결과: <출력>/ProxmoxClient-macos-<arch>.zip (안에 "Proxmox Client.app")
# 서명: 실행 파일은 .NET 이 임시(ad-hoc) 서명한다. 개발자 ID 서명·공증은 macOS 에서 codesign/notarytool 로 따로.
#       서명·공증 없이 받은 앱은 macOS 가 막으므로 처음 한 번: xattr -cr "/Applications/Proxmox Client.app"
set -euo pipefail
ARCH="${1:-arm64}"
OUT="$(realpath -m "${2:-dist}")"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="osx-${ARCH}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/ProxmoxClient.Desktop/ProxmoxClient.Desktop.csproj" | head -1)"
WORK="$(mktemp -d)"
APP="$WORK/Proxmox Client.app"

dotnet publish "$ROOT/ProxmoxClient.Desktop" -c Release -r "$RID" --self-contained true \
    -p:UseAppHost=true -p:PublishSingleFile=false -o "$WORK/publish" >/dev/null

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$WORK/publish/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/ProxmoxClient"

# 아이콘(.icns) — 앱 아이콘 PNG 에서 만든다(Pillow)
python3 - "$ROOT/ProxmoxClient.UI/Assets/AppIcon.png" "$APP/Contents/Resources/AppIcon.icns" <<'PY'
import sys
from PIL import Image
img = Image.open(sys.argv[1]).convert("RGBA")
size = max(img.size)
canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
canvas.paste(img, ((size - img.width) // 2, (size - img.height) // 2))
canvas.resize((1024, 1024), Image.LANCZOS).save(sys.argv[2], format="ICNS")
PY

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Proxmox Client</string>
    <key>CFBundleDisplayName</key><string>Proxmox Client</string>
    <key>CFBundleIdentifier</key><string>io.github.moonjm98.proxmoxclient</string>
    <key>CFBundleExecutable</key><string>ProxmoxClient</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
    <key>NSHumanReadableCopyright</key><string>Proxmox Client</string>
</dict>
</plist>
PLIST

mkdir -p "$OUT"
ZIP="$OUT/ProxmoxClient-${VERSION}-macos-${ARCH}.zip"
rm -f "$ZIP"
# zip 은 실행 권한·심볼릭 링크를 보존한다
(cd "$WORK" && zip -qry "$ZIP" "Proxmox Client.app")
rm -rf "$WORK"
echo "$ZIP"
