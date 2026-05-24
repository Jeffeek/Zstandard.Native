#!/bin/bash

set -e

CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
RED='\033[0;31m'
GRAY='\033[0;90m'
NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/../.."

VERSION="${1:-0.0.0-local-test}"
PROJECT="${2:-All}"   # Core | Runtimes | All

VALID_PROJECTS="Core Runtimes All"
if [[ ! " $VALID_PROJECTS " =~ " $PROJECT " ]]; then
    echo -e "${RED}Error: Invalid project '$PROJECT'${NC}"
    echo -e "${YELLOW}Valid values: $VALID_PROJECTS${NC}"
    exit 1
fi

OUTPUT_DIR="./test-packages"
EXTRACT_DIR="./test-extract"

echo -e "${CYAN}======================================${NC}"
echo -e "${CYAN}  NuGet Package Builder & Inspector${NC}"
echo -e "${CYAN}======================================${NC}"
echo ""
echo -e "${GREEN}Version:     $VERSION${NC}"
echo -e "${GREEN}Project:     $PROJECT${NC}"
echo -e "${GREEN}Output Dir:  $OUTPUT_DIR${NC}"
echo ""

[ -d "$OUTPUT_DIR" ]  && { echo -e "${YELLOW}Cleaning previous packages...${NC}"; rm -rf "$OUTPUT_DIR"; }
[ -d "$EXTRACT_DIR" ] && { echo -e "${YELLOW}Cleaning previous extracts...${NC}"; rm -rf "$EXTRACT_DIR"; }
mkdir -p "$OUTPUT_DIR"

# ----------------------------------------------------------------
# Pack: Core (managed-only)
# ----------------------------------------------------------------
if [[ "$PROJECT" == "Core" || "$PROJECT" == "All" ]]; then
    echo ""
    echo -e "${YELLOW}======================================${NC}"
    echo -e "${YELLOW}  Building Zstandard.Native (managed)${NC}"
    echo -e "${YELLOW}======================================${NC}"
    if ! dotnet pack src/Zstandard.Native/Zstandard.Native.csproj \
            -c Release --output "$OUTPUT_DIR" -p:PackageVersion="$VERSION"; then
        echo -e "${RED}Error: Core pack failed!${NC}"; exit 1
    fi
fi

# ----------------------------------------------------------------
# Pack: per-RID runtime packages
# ----------------------------------------------------------------
if [[ "$PROJECT" == "Runtimes" || "$PROJECT" == "All" ]]; then
    for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
        echo ""
        echo -e "${YELLOW}======================================${NC}"
        echo -e "${YELLOW}  Building Zstandard.Native.$rid${NC}"
        echo -e "${YELLOW}======================================${NC}"
        if ! dotnet pack src/Zstandard.Native.Runtime/Zstandard.Native.Runtime.csproj \
                -c Release --output "$OUTPUT_DIR" \
                -p:PackageVersion="$VERSION" \
                -p:RuntimePackageRid="$rid"; then
            echo -e "${RED}Error: $rid pack failed!${NC}"; exit 1
        fi
    done

    # Pack the meta-package last (depends on per-RID packages being in OutputDir)
    echo ""
    echo -e "${YELLOW}======================================${NC}"
    echo -e "${YELLOW}  Building Zstandard.Native.Runtimes${NC}"
    echo -e "${YELLOW}======================================${NC}"

    ABS_OUTPUT="$(cd "$OUTPUT_DIR" && pwd)"

    # Write a temporary NuGet.config pointing restore at the just-packed packages
    cat > "$OUTPUT_DIR/NuGet.config" << EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$ABS_OUTPUT" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="Zstandard.Native.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

    if ! dotnet pack src/Zstandard.Native.Runtimes/Zstandard.Native.Runtimes.csproj \
            -c Release --output "$OUTPUT_DIR" -p:PackageVersion="$VERSION"; then
        echo -e "${RED}Error: Runtimes meta-package pack failed!${NC}"; exit 1
    fi
    rm -f "$OUTPUT_DIR/NuGet.config"
fi

echo ""
echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}  All packages built successfully!${NC}"
echo -e "${GREEN}======================================${NC}"
echo ""

echo -e "${YELLOW}Created packages:${NC}"
for NUPKG in "$OUTPUT_DIR"/*.nupkg; do
    [ -f "$NUPKG" ] || continue
    SIZE_KB=$(du -k "$NUPKG" | cut -f1)
    echo -e "${CYAN}  $(basename "$NUPKG") (${SIZE_KB} KB)${NC}"
done

echo ""
echo -e "${YELLOW}Inspecting packages...${NC}"

for NUPKG in "$OUTPUT_DIR"/*.nupkg; do
    [ -f "$NUPKG" ] || continue
    PACKAGE_NAME=$(basename "$NUPKG" .nupkg)
    EXTRACT_PATH="$EXTRACT_DIR/$PACKAGE_NAME"

    echo ""
    echo -e "${YELLOW}Extracting $(basename "$NUPKG")...${NC}"
    mkdir -p "$EXTRACT_PATH"
    unzip -q "$NUPKG" -d "$EXTRACT_PATH"

    echo -e "${CYAN}Package contents for $PACKAGE_NAME:${NC}"
    echo ""
    find "$EXTRACT_PATH" -type f \( -name "*.dll" -o -name "*.xml" -o -name "*.md" -o -name "libzstd.*" \) | while read -r FILE; do
        SIZE_KB=$(du -k "$FILE" | cut -f1)
        REL="${FILE#$EXTRACT_PATH/}"
        echo -e "${GRAY}  $REL (${SIZE_KB} KB)${NC}"
    done
done

echo ""
echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}  Package inspection completed!${NC}"
echo -e "${GREEN}======================================${NC}"
echo ""

echo -e "${YELLOW}Verification:${NC}"
ALL_VALID=true

for NUPKG in "$OUTPUT_DIR"/*.nupkg; do
    [ -f "$NUPKG" ] || continue
    PACKAGE_NAME=$(basename "$NUPKG" .nupkg)
    PKG_ID=$(echo "$PACKAGE_NAME" | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+.*$//')
    EXTRACT_PATH="$EXTRACT_DIR/$PACKAGE_NAME"

    echo -e "${CYAN}  $PKG_ID:${NC}"

    if echo "$PKG_ID" | grep -qE '^Zstandard\.Native\.(win|linux|osx)-'; then
        # Per-RID: expect a native binary
        NATIVE_COUNT=$(find "$EXTRACT_PATH" -type f \( -name "libzstd.dll" -o -name "libzstd.so" -o -name "libzstd.dylib" \) | wc -l)
        if [ "$NATIVE_COUNT" -gt 0 ]; then
            NATIVE_FILE=$(find "$EXTRACT_PATH" -type f \( -name "libzstd.dll" -o -name "libzstd.so" -o -name "libzstd.dylib" \) | head -1)
            echo -e "${GREEN}    [OK] Native binary: ${NATIVE_FILE#$EXTRACT_PATH/}${NC}"
        else
            RID="${PKG_ID#Zstandard.Native.}"
            echo -e "${YELLOW}    [WARN] No native binary for $RID — was it fetched before packing?${NC}"
        fi

    elif [ "$PKG_ID" = "Zstandard.Native.Runtimes" ]; then
        # Meta-package: expect <dependency> entries in the nuspec
        NUSPEC=$(find "$EXTRACT_PATH" -name "*.nuspec" | head -1)
        if [ -n "$NUSPEC" ] && grep -q '<dependency' "$NUSPEC"; then
            DEP_COUNT=$(grep -c '<dependency' "$NUSPEC" || true)
            echo -e "${GREEN}    [OK] nuspec contains $DEP_COUNT <dependency> entries${NC}"
        else
            echo -e "${RED}    [MISSING] nuspec has no <dependency> entries!${NC}"
            ALL_VALID=false
        fi

    else
        # Managed package: expect README and Zstandard.Native.dll
        if find "$EXTRACT_PATH" -type f -name "README.nuget.md" | grep -q .; then
            echo -e "${GREEN}    [OK] README.nuget.md found${NC}"
        else
            echo -e "${RED}    [MISSING] README.nuget.md not found!${NC}"
            ALL_VALID=false
        fi

        DLL_COUNT=$(find "$EXTRACT_PATH" -type f -name "Zstandard.Native.dll" | wc -l)
        if [ "$DLL_COUNT" -gt 0 ]; then
            echo -e "${GREEN}    [OK] Zstandard.Native.dll found in $DLL_COUNT TFM(s)${NC}"
        else
            echo -e "${RED}    [MISSING] Zstandard.Native.dll not found!${NC}"
            ALL_VALID=false
        fi
    fi
done

echo ""
[ "$ALL_VALID" = true ] && echo -e "${GREEN}All packages valid!${NC}" || echo -e "${YELLOW}Warning: some packages have missing files.${NC}"

echo ""
echo -e "${YELLOW}Next steps:${NC}"
echo -e "${GRAY}  - Core only:         ./NugetPackage.sh <version> Core${NC}"
echo -e "${GRAY}  - Runtime pkgs only: ./NugetPackage.sh <version> Runtimes${NC}"
echo -e "${GRAY}  - Build all:         ./NugetPackage.sh <version> All${NC}"
echo -e "${GRAY}  - Test locally:      dotnet add package Zstandard.Native --source ./test-packages${NC}"
echo ""
