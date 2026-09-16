$api = 'http://127.0.0.1:32145'
$body = @{
    title = 'CC'
    text = 'Working'
    secondaryText = '128k · 18m'
    detail = 'Refactoring retrieval pipeline'
    progress = 0.63
    priority = 80
    ttlSeconds = 10
    wakeOnUpdate = $true
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Put `
    -Uri "$api/api/v1/items/demo" `
    -ContentType 'application/json' `
    -Body $body
