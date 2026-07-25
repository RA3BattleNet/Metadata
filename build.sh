#!/usr/bin/env bash
set -euo pipefail

# Stage A: pure managed flatten + validate
# Stage B: WebP only when RUN_STAGE_B=1 (CF publish path)

curl -sSL https://dot.net/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh -c 10.0 -InstallDir ./dotnet
./dotnet/dotnet --version

./dotnet/dotnet run --project ./Ra3.BattleNet.Metadata --no-launch-profile -- \
  build --src="./Metadata" --dst="./Output"

if [ "${RUN_STAGE_B:-1}" = "1" ]; then
  echo "Running Stage B (WebP)..."
  ./dotnet/dotnet run --project ./Ra3.BattleNet.Metadata.StageB --no-launch-profile -- \
    --dst="./Output"
else
  echo "Skipping Stage B (set RUN_STAGE_B=1 to enable WebP)."
fi
