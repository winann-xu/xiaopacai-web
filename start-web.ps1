# 本地 Web 3.0 启动脚本（测试用）：注入强 JWT 密钥后以 Production 配置启动
$env:Jwt__SecretKey = "local-e2e-secret-key-32chars-2026x"
Start-Process -FilePath "C:\dotnet\dotnet.exe" `
    -ArgumentList "C:\Users\Public\bridge\work\xiaopacai-web\server\bin\Release\net8.0\XiaopacaiWeb.dll" `
    -WorkingDirectory "C:\Users\Public\bridge\work\xiaopacai-web\server" `
    -WindowStyle Hidden
Write-Output "Web started with strong JWT key"
