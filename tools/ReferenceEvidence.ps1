function Read-ReferenceEvidence {
  param([Parameter(Mandatory)][string] $WorkRoot)

  $registryPath = Join-Path $PSScriptRoot '../tests/Compatibility/oracle-evidence.json'
  $registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -AsHashtable
  $cases = [ordered]@{}
  foreach ($stage in $registry.Keys) {
    if ($stage -cnotin @('differential', 'parser')) { throw "Unknown oracle evidence stage: $stage" }
    $log = "$stage.log"
    $witnesses = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($witness in $registry[$stage].Values) { $witnesses[$witness] = 0 }
    $number = 0
    foreach ($line in [IO.File]::ReadLines((Join-Path $WorkRoot $log))) {
      $number++
      if ($witnesses.ContainsKey($line) -and $witnesses[$line] -eq 0) { $witnesses[$line] = $number }
    }
    foreach ($id in $registry[$stage].Keys) {
      $witness = $registry[$stage][$id]
      $lineNumber = $witnesses[$witness]
      if ($lineNumber -eq 0) { throw "Missing oracle outcome for '$id' in $log. Expected: $witness" }
      if ($cases.Contains($id)) { throw "Duplicate oracle evidence ID: $id" }
      $cases[$id] = [ordered]@{ Log = $log; Line = $lineNumber }
    }
  }
  return $cases
}
