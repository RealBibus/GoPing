$p='C:\GoPing\GoPing\goping\GoPing.Messenger\MainWindow.xaml.cs'
$c=[IO.File]::ReadAllText($p)
$c=$c.Replace("            UpdateCallVideoVisibility(false);`r`n","")
$c=$c.Replace("            UpdateCallVideoVisibility(true);`r`n","")
$c=$c.Replace("            UpdateCallVideoVisibility();`r`n","")
$c=$c.Replace("                    UpdateCallVideoVisibility(_calls.IsScreenSharing ? true : false);`r`n","")
$c=$c.Replace("                UpdateCallVideoVisibility();`r`n","")
$c=$c.Replace("        UpdateCallVideoVisibility();`r`n","")
$c=$c.Replace("        UpdateCallVideoVisibility(false);`r`n","")
$c=$c.Replace("UpdateCallVideoVisibility(false);","")
$c=$c.Replace("UpdateCallVideoVisibility(true);","")
$c=$c.Replace("UpdateCallVideoVisibility();","")
[IO.File]::WriteAllText($p,$c,[Text.UTF8Encoding]::new($true))
Write-Output ok
