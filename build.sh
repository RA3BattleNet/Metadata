#!/usr/bin/env bash
set -euo pipefail

# 默认仅核心；--webp 时核心 CLI 内部串联 Imaging

WEBP_FLAG=()
BUILD_ARGS=()
for arg in "$@"; do
  case "$arg" in
    --webp|--publish|--with-webp) WEBP_FLAG=(--webp) ;;
    --content-revision=*) BUILD_ARGS+=("$arg") ;;
    --help|-h)
      cat <<'EOF'
用法: bash build.sh [--webp]

  (无参数)   仅核心构建 → ./Output
  --webp     核心 + Imaging（WebP 与 Hash 更新）
  --publish  同 --webp
  --content-revision=VALUE  指定发布内容版本
EOF
      exit 0
      ;;
  esac
done

# .NET SDK：Cloudflare 构建镜像里没有 dotnet，只能现场装。官方通道（dot.net →
# builds.dotnet.microsoft.com）在构建容器里会被中途重置（curl 35/56），所以改从自己的
# 对象存储取同一个官方 tarball，仍校验官方 sha512。
DOTNET_SDK_VERSION="10.0.404"
DOTNET_SDK_TARBALL="dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz"
DOTNET_SDK_URL="https://update.ra3battle.net/app/redist/${DOTNET_SDK_TARBALL}"
DOTNET_SDK_SHA512="577d0eb17fec33d029104c5c63a1ab0accc3e28776274abef2218dbac5af18d2109b5406d26b31d76f5c245215e3ba0926a94c20246c672c41847c9e1eb1dc50"

SDK_ROOT="$PWD/dotnet"
SDK_CACHE="${TMPDIR:-/tmp}/${DOTNET_SDK_TARBALL}"
mkdir -p "$SDK_ROOT"

curl -fsSL --retry 5 --retry-all-errors --retry-delay 3 --connect-timeout 20 \
  -o "$SDK_CACHE" "$DOTNET_SDK_URL"
echo "${DOTNET_SDK_SHA512}  ${SDK_CACHE}" | sha512sum -c -
tar -xzf "$SDK_CACHE" -C "$SDK_ROOT"

# 核心 CLI 内部会再 spawn `dotnet`（Imaging），必须显式导出
export DOTNET_ROOT="$SDK_ROOT"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --version

dotnet run --project ./Ra3.BattleNet.Metadata --no-launch-profile -- \
  build --src="./Metadata" --dst="./Output" "${WEBP_FLAG[@]}" "${BUILD_ARGS[@]}"
