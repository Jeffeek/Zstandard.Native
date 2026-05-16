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
PROJECT="${2:-All}"

echo -e "${CYAN}======================================${NC}"
echo -e "${CYAN}  NuGet Package Builder & Inspector${NC}"
echo -e "${CYAN}======================================${NC}"
echo ""

declare -A PROJECTS
PROJECTS["Core"]="src/Zstandard.Native/Zstandard.Native.csproj"

VALID_PROJECTS="Core All"
if [[ ! " $VALID_PROJECTS " =~ " $PROJECT " ]]; then
    echo -e "${RED}Error: Invalid project '$PROJECT'${NC}"
    echo -e "${YELLOW}Valid projects: $VALID_PROJECTS${NC}"
    exit 1
fi

if [ "$PROJECT" == "All" ]; then
    PROJECTS_TO_PACK=("${PROJECTS[@]}")
else
    PROJECTS_TO_PACK=("${PROJECTS[$PROJECT]}")
fi

OUTPUT_DIR="./test-packages"
EXTRACT_DIR="./test-extract"

echo -e "${GREEN}Version:       $VERSION${NC}"
echo -e "${GREEN}Projects:      $PROJECT (${#PROJECTS_TO_PACK[@]} package(s))${NC}"
echo -e "${GREEN}Output Dir:    $OUTPUT_DIR${NC}"
echo -e "${GREEN}Extract Dir:   $EXTRACT_DIR${NC}"
echo ""

[ -d "$OUTPUT_DIR" ]  && { echo -e "${YELLOW}Cleaning previous packages...${NC}"; rm -rf "$OUTPUT_DIR"; }
[ -d "$EXTRACT_DIR" ] && { echo -e "${YELLOW}Cleaning previous extracts...${NC}"; rm -rf "$EXTRACT_DIR"; }
mkdir -p "$OUTPUT_DIR"

PACKAGE_COUNT=0
TOTAL=${#PROJECTS_TO_PACK[@]}

for PROJECT_PATH in "${PROJECTS_TO_PACK[@]}"; do
    ((PACKAGE_COUNT++))
    PROJECT_NAME=$(basename "$PROJECT_PATH" .csproj)

    echo ""
    echo -e "${YELLOW}======================================${NC}"
    echo -e "${YELLOW}  Building $PROJECT_NAME ($PACKAGE_COUNT/$TOTAL)${NC}"
    echo -e "${YELLOW}======================================${NC}"
    echo ""

    if ! dotnet pack "$PROJECT_PATH" -c Release --output "$OUTPUT_DIR" -p:PackageVersion="$VERSION"; then
        echo -e "${RED}Error: Package build failed for $PROJECT_NAME!${NC}"
        exit 1
    fi

    echo -e "${GREEN}$PROJECT_NAME package built successfully!${NC}"
done

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
    find "$EXTRACT_PATH" -type f \( -name "*.dll" -o -name "*.xml" -o -name "README.md" -o -name "libzstd.*" \) | while read -r FILE; do
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
    EXTRACT_PATH="$EXTRACT_DIR/$PACKAGE_NAME"

    echo -e "${CYAN}  $PACKAGE_NAME:${NC}"

    if find "$EXTRACT_PATH" -type f -name "README.md" | grep -q .; then
        echo -e "${GREEN}    [OK] README.md found${NC}"
    else
        echo -e "${RED}    [MISSING] README.md not found!${NC}"
        ALL_VALID=false
    fi

    PROJECT_NAME=$(echo "$PACKAGE_NAME" | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+.*$//')
    DLL_NAME="$PROJECT_NAME.dll"
    DLL_COUNT=$(find "$EXTRACT_PATH" -type f -name "$DLL_NAME" | wc -l)
    if [ "$DLL_COUNT" -gt 0 ]; then
        echo -e "${GREEN}    [OK] $DLL_NAME found in $DLL_COUNT target(s)${NC}"
    else
        echo -e "${RED}    [MISSING] $DLL_NAME not found!${NC}"
        ALL_VALID=false
    fi

    # Native binaries: warn-only — RID-specific, depends on which host produced the pack.
    NATIVE_COUNT=$(find "$EXTRACT_PATH" -type f \( -name "libzstd.dll" -o -name "libzstd.so" -o -name "libzstd.dylib" \) | wc -l)
    if [ "$NATIVE_COUNT" -gt 0 ]; then
        echo -e "${GREEN}    [OK] Native binaries: $NATIVE_COUNT file(s)${NC}"
    else
        echo -e "${YELLOW}    [WARN] No native libzstd binaries bundled — consumers must supply their own.${NC}"
    fi
done

echo ""
[ "$ALL_VALID" = true ] && echo -e "${GREEN}All packages valid!${NC}" || echo -e "${YELLOW}Warning: some packages have missing files.${NC}"

echo ""
echo -e "${YELLOW}Next steps:${NC}"
echo -e "${GRAY}  - Build specific:    ./NugetPackage.sh <version> Core${NC}"
echo -e "${GRAY}  - Build all:         ./NugetPackage.sh <version> All${NC}"
echo -e "${GRAY}  - Test locally:      dotnet add package Zstandard.Native --source ./test-packages${NC}"
echo ""
