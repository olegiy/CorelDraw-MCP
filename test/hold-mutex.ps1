$lock = New-Object Threading.Mutex($false, 'Local\CorelDrawMcp_v27')
$owned = $false
try {
    try { $owned = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Test could not acquire the CorelDRAW mutex.' }
    [Console]::WriteLine('READY')
    $null = [Console]::ReadLine()
}
finally { if ($owned) { $lock.ReleaseMutex() }; $lock.Dispose() }
