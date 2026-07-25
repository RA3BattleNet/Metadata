#!/usr/bin/env bash
set -euo pipefail

# 默认仅核心；--webp 时核心 CLI 内部串联 Imaging

WEBP_FLAG=()
for arg in "$@"; do
  case "$arg" in
    --webp|--publish|--with-webp) WEBP_FLAG=(--webp) ;;
    --help|-h)
      cat <<'EOF'
用法: bash build.sh [--webp]

  (无参数)   仅核心构建 → ./Output
  --webp     核心 + Imaging（WebP 与 Hash 更新）
  --publish  同 --webp
EOF
      exit 0
      ;;
  esac
done

curl -sSL https://dot.net/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh -c 10.0 -InstallDir ./dotnet
./dotnet/dotnet --version

./dotnet/dotnet run --project ./Ra3.BattleNet.Metadata --no-launch-profile -- \
  build --src="./Metadata" --dst="./Output" "${WEBP_FLAG[@]+"${WEBP_FLAG[@]}"}"
