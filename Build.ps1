param([string]$Configuration = "Debug")
& 'C:\Program Files\dotnet\dotnet.exe' build "$PSScriptRoot\IkosAegis.csproj" --configuration $Configuration
