$paths = @(
    'c:\Users\tyler\OneDrive\Documents\AORebirth\tools-temp\aodb-nupkg\lib\net10.0\AODB.Common.dll',
    'c:\Users\tyler\OneDrive\Documents\AORebirth\tools-temp\aodb-nupkg\lib\netstandard2.1\AODB.Common.dll'
)

foreach ($path in $paths) {
    Write-Output ("DLL " + $path)
    try {
        $common = [Reflection.Assembly]::LoadFrom($path)
        Write-Output ("  FullName " + $common.FullName)
        Write-Output ("  Exported " + $common.GetExportedTypes().Count)
        $common.GetExportedTypes() | Where-Object { $_.Name -match 'Wall|Tilemap' } | ForEach-Object { Write-Output ("  " + $_.FullName) }
    } catch {
        Write-Output ("  FAIL " + $_.Exception.GetType().FullName + " " + $_.Exception.Message)
        if ($_.Exception.LoaderExceptions) {
            $_.Exception.LoaderExceptions | Select-Object -First 8 | ForEach-Object { Write-Output ("  LE " + $_.Message) }
        }
        if ($_.Exception.InnerException) {
            Write-Output ("  INNER " + $_.Exception.InnerException.Message)
        }
    }
}
