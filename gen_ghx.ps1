$L = 3000
$W = 4000
$T = 100

$guid1 = [guid]::NewGuid().ToString("B").ToUpper()
$guid2 = [guid]::NewGuid().ToString("B").ToUpper()
$guid3 = [guid]::NewGuid().ToString("B").ToUpper()
$docid = [guid]::NewGuid().ToString("B").ToUpper()

@"
<?xml version="1.0" encoding="utf-8"?>
<Archive name=":hQAAAEdpem1vAHJpZ2h0AEJvdHRvbQ==">
  <chunk name="Definition">
    <items>
      <item type="int" name="ghenv.ComponentVersion">2</item>
      <item type="guid" name="ghenv.DocumentGuid">$docid</item>
      <item type="string" name="ghenv.Description">Hanako Parametric Floor</item>
    </items>
    <chunk name="Document">
      <items>
        <item type="int" name="ghenv.SolutionMode">0</item>
        <item type="bool" name="ghenv.EnableSolver">true</item>
        <item type="int" name="ghenv.ObjectListVersion">1</item>
      </items>
    </chunk>
  </chunk>
</Archive>
"@ | Out-File -FilePath "D:/-A-hanako/gh-bridge/test.ghx" -Encoding UTF8
Write-Host "Done: D:/-A-hanako/gh-bridge/test.ghx"