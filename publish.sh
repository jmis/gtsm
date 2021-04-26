rm -fdr ./out/
dotnet publish -o ./out/linux-x64 -r linux-x64 -c Publish
chmod +x ./out/linux-x64/gtsm
