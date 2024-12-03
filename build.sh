curl -sSL https://dot.net/v1/dotnet-install.sh > dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh -c 8.0 -InstallDir ./dotnet
./dotnet/dotnet --version
./dotnet/dotnet run --project ./Ra3.BattleNet.Metadata --src="./Metadata" --dst="./output"
