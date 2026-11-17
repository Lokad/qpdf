# Structural xrefs are physical sections; a retained dictionary tag is ordinary data.
function Get-XrefRolePayload {
  param([byte[]] $Ciphertext, [byte[]] $FileKey, [int] $Revision, [int] $Number, [int] $Generation)
  $key = $FileKey
  if ($Revision -eq 4) {
    [byte[]] $material = @($FileKey) + @([byte]($Number -band 255), [byte](($Number -shr 8) -band 255), [byte](($Number -shr 16) -band 255), [byte]($Generation -band 255), [byte](($Generation -shr 8) -band 255), 115, 65, 108, 84)
    $key = [Security.Cryptography.MD5]::HashData($material)
  }
  if ($Ciphertext.Length -lt 32 -or ($Ciphertext.Length % 16) -ne 0) { throw 'Tagged stream is not a complete AES packet.' }
  $aes = [Security.Cryptography.Aes]::Create()
  try {
    $aes.Key = $key; $aes.IV = [byte[]]$Ciphertext[0..15]
    $decryptor = $aes.CreateDecryptor()
    try { return ,$decryptor.TransformFinalBlock($Ciphertext, 16, $Ciphertext.Length - 16) }
    finally { $decryptor.Dispose() }
  } finally { $aes.Dispose() }
}

function Assert-XrefRoleOutput {
  param([string] $Case, [string] $File, [int] $Revision, [bool] $Linearized)
  foreach ($password in @('reader', $(if ($Revision -eq 4) { 'fixture-owner' } else { 'owner' }))) {
    if ((Invoke-Oracle $Case @("--password=$password", '--check', $File)) -ne 0) { throw 'Tagged-stream encrypted structure check failed.' }
  }
  if ($Linearized) {
    $code = Invoke-Oracle $Case @('--password=reader', '--check-linearization', $File)
    if ($code -ne 0 -or (Get-Content -LiteralPath "$Case/ora-stdout.txt" -Raw) -notmatch 'no linearization errors') { throw 'Tagged-stream linearization did not validate.' }
  }
  $code = Invoke-Oracle $Case @('--password=reader', '--json=2', '--json-key=qpdf', '--json-key=encrypt', '--json-stream-data=inline', '--decode-level=none', '--show-encryption-key', $File)
  if ($code -ne 0) { throw 'Tagged-stream native JSON inspection failed.' }
  $report = Get-Content -LiteralPath "$Case/ora-stdout.txt" -Raw | ConvertFrom-Json -AsHashtable
  $entries = @($report.qpdf[1].GetEnumerator() | Where-Object { $_.Value.Contains('stream') -and $_.Value.stream.dict.Contains('/Note') })
  if ($entries.Count -ne 1 -or $entries[0].Value.stream.dict['/Note'] -cne 'u:private-header-secret') { throw 'Native did not recover the exact private header.' }
  if ($report.encrypt.parameters.R -ne $Revision) { throw 'Tagged-stream output revision differs.' }
  $pair = [regex]::Match($entries[0].Key, '^obj:(\d+) (\d+) R$')
  $cipher = [Convert]::FromBase64String($entries[0].Value.stream.data)
  $key = [Convert]::FromHexString($report.encrypt.parameters.key)
  if ($entries[0].Value.stream.dict['/Type'] -ceq '/XRef') {
    $plain = Get-XrefRolePayload $cipher $key $Revision ([int]$pair.Groups[1].Value) ([int]$pair.Groups[2].Value)
    $damaged = [byte[]]$cipher.Clone(); $damaged[0] = $damaged[0] -bxor 1
    $negative = Get-XrefRolePayload $damaged $key $Revision ([int]$pair.Groups[1].Value) ([int]$pair.Groups[2].Value)
  } else {
    # QDF's native equivalent omits the nonstructural tag, so native JSON has
    # already decrypted its body. The managed tagged output still uses AES above.
    $plain = $cipher; $negative = [byte[]]$plain.Clone(); $negative[0] = $negative[0] -bxor 1
  }
  if ([Text.Encoding]::ASCII.GetString($plain) -cne 'private-stream-secret') { throw 'Independent AES decoding changed the private payload.' }
  if ([Text.Encoding]::ASCII.GetString($negative) -ceq 'private-stream-secret') { throw 'Mutated-IV payload negative control passed.' }
  # Native itself leaves this private tagged payload encoded in raw JSON. The
  # native key plus system AES establishes its bytes without claiming raw parity.
  $report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath ($File + '.reference.json') -Encoding utf8NoBOM
}

function Invoke-ReferenceXrefRoles {
  $source = New-Case 'xref-role-source'
  Copy-Item -LiteralPath "$fixtureDir/security-r6-xref-private.pdf" -Destination "$source/inputs/a.pdf"
  $read = Invoke-SecurityManaged $source @('emit', '--workdir', $source, '--', 'inputs/a.pdf', '--password=reader', '--json=2', '--json-key=qpdf', '--json-stream-data=inline', '--decode-level=none')
  if ($read.ExitCode -ne 0) { throw 'Recorded tagged-stream input failed managed inspection.' }
  $table = ($read.Text | ConvertFrom-Json -AsHashtable).qpdf[1]
  $private = $table['obj:3 0 R'].stream
  if ($private.dict['/Note'] -cne 'u:private-header-secret' -or [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($private.data)) -cne 'private-stream-secret') { throw 'Recorded native tagged-stream plaintext differs.' }
  $model = '{"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Private":"4 0 R"}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},"obj:4 0 R":{"stream":{"dict":{"/Type":"/XRef","/Note":"u:private-header-secret"},"data":"cHJpdmF0ZS1zdHJlYW0tc2VjcmV0"}},"trailer":{"value":{"/Root":"1 0 R","/Size":5}}}]}'
  [IO.File]::WriteAllText("$source/model.json", $model, [Text.UTF8Encoding]::new($false))
  if ((Invoke-Oracle $source @('--json-input', "$source/model.json", '--object-streams=disable', '--stream-data=preserve', "$source/plain.pdf")) -ne 0) { throw 'Tagged-stream authored source creation failed.' }
  [IO.File]::WriteAllText("$source/equivalent.json", $model.Replace('"/Type":"/XRef",', ''), [Text.UTF8Encoding]::new($false))
  if ((Invoke-Oracle $source @('--json-input', "$source/equivalent.json", '--object-streams=disable', '--stream-data=preserve', "$source/equivalent.pdf")) -ne 0) { throw 'Untagged equivalent creation failed.' }
  $cases = @(
    @{ Name = 'classic'; Flags = @('--object-streams=disable'); Revision = 6 },
    @{ Name = 'packed'; Flags = @('--object-streams=generate'); Revision = 6 },
    @{ Name = 'linear'; Flags = @('--linearize'); Revision = 6 },
    @{ Name = 'linear-packed'; Flags = @('--linearize', '--object-streams=generate'); Revision = 6 },
    @{ Name = 'qdf'; Flags = @('--qdf'); Revision = 6 },
    @{ Name = 'qdf-packed'; Flags = @('--qdf', '--object-streams=generate'); Revision = 6 },
    @{ Name = 'template4'; Flags = @('--qdf', '--object-streams=generate'); Revision = 4 }
  )
  foreach ($row in $cases) {
    $name = 'xref-role-' + $row.Name; $case = New-Case $name
    Copy-Item -LiteralPath "$source/plain.pdf" -Destination "$case/inputs/a.pdf"
    $before = (Get-FileHash -LiteralPath "$case/inputs/a.pdf" -Algorithm SHA256).Hash
    if ($row.Revision -eq 4) {
      Copy-Item -LiteralPath "$fixtureDir/security-r4-ascii.pdf" -Destination "$case/inputs/template.pdf"
      $security = @('--copy-encryption=inputs/template.pdf', '--encryption-file-password=fixture-owner')
    } else { $security = @('--encrypt', 'reader', 'owner', '256', '--') }
    $options = @('--stream-data=preserve') + $row.Flags + $security
    $managed = Invoke-SecurityManaged $case (@('emit', '--workdir', $case, '--out', "outputs/a.pdf=$case/lib.pdf", '--', 'inputs/a.pdf') + $options + @('outputs/a.pdf'))
    if ($managed.ExitCode -ne 0 -or $managed.Status -cne 'Completed') { throw "$name managed publication failed." }
    if ($row.Flags -contains '--qdf') {
      if ((Invoke-Oracle $case (@('inputs/a.pdf') + $options + @("$case/tagged-native.pdf"))) -ne 0) { throw "$name native tagged QDF creation failed." }
      $code = Invoke-Oracle $case @('--password=reader', '--check', "$case/tagged-native.pdf")
      if ($code -ne 3 -or -not (Get-Content -LiteralPath "$case/ora-stderr.txt" -Raw).Contains('bad indirect reference (0 0 R)')) { throw 'Native tagged QDF negative control changed.' }
      Copy-Item -LiteralPath "$case/ora-stderr.txt" -Destination "$case/tagged-native-check.txt"
      Copy-Item -LiteralPath "$source/equivalent.pdf" -Destination "$case/inputs/equivalent.pdf"
      if ((Invoke-Oracle $case (@('inputs/equivalent.pdf') + $options + @("$case/ora.pdf"))) -ne 0) { throw "$name native equivalent publication failed." }
    } elseif ((Invoke-Oracle $case (@('inputs/a.pdf') + $options + @("$case/ora.pdf"))) -ne 0) { throw "$name native publication failed." }
    foreach ($side in @('lib', 'ora')) { Assert-XrefRoleOutput $case "$case/$side.pdf" $row.Revision ($row.Name.StartsWith('linear')) }
    if ((Get-FileHash -LiteralPath "$case/inputs/a.pdf" -Algorithm SHA256).Hash -cne $before) { throw "$name changed the source." }
    Write-Output "[$name] encrypted headers, payload comparison, physical layout and mutation refusal pass; QDF uses an explicit native equivalent"
  }
}
