#!/usr/bin/env bash
set -euo pipefail

# 默认：纯 managed 构建（展平 + 校验）
# 发布：bash build.sh --webp  （再跑 Imaging / WebP）

WITH_WEBP=0
for arg in "$@"; do
  case "$arg" in
    --webp|--publish|--with-webp) WITH_WEBP=1 ;;
    --help|-h)
      cat <<'EOF'
用法: bash build.sh [--webp]

  (无参数)   仅核心构建 → ./Output
  --webp     核心构建后执行 Imaging（WebP 转码）
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

echo ">>> 核心构建 (build)"
./dotnet/dotnet run --project ./Ra3.BattleNet.Metadata --no-launch-profile -- \
  build --src="./Metadata" --dst="./Output"

if [ "$WITH_WEBP" = "1" ]; then
  echo ">>> Imaging (WebP)"
  ./dotnet/dotnet run --project ./Ra3.BattleNet.Metadata.Imaging --no-launch-profile -- \
    --dst="./Output"
else
  echo ">>> 跳过 Imaging（发布请用: bash build.sh --webp）"
fi
