#Defining preferences variables
$config = (Get-Content "config.json" -Raw) | ConvertFrom-Json
$wantedImageName = $config.WantedWindowsEdition
$unwantedProvisionnedPackages = $config.ProvisionnedPackagesToRemove
$unwantedWindowsPackages = $config.WindowsPackagesToRemove

#Defining system variables
$rootWorkdir = "c:\tiny11\"
$isoFolder = $rootWorkdir + "iso\"
$installImageFolder = $rootWorkdir + "installimage\"
$bootImageFolder = $rootWorkdir + "bootimage\"
$isoPath = "c:\windows11.iso"

#Downloading the Windows 11 ISO using WindowsIsoDownloader
#Start-Process ./tools/WindowsIsoDownloader/WindowsIsoDownloader.exe -NoNewWindow -Wait

#Mount the Windows 11 ISO
$mountResult = Mount-DiskImage -ImagePath $isoPath
$isoDriveLetter = ($mountResult | Get-Volume).DriveLetter

#Creating needed temporary folders
md $rootWorkdir
md $isoFolder
md $installImageFolder
md $bootImageFolder

#Copying the ISO files to the ISO folder
cp -Recurse ($isoDriveLetter + ":\*") $isoFolder

#Unmounting the original ISO since we don't need it anymore (we have a copy of the content)
Dismount-DiskImage -ImagePath $isoPath

################# Beginning of install.wim patches ##################
#Getting the wanted image index
$wantedImageIndex = Get-WindowsImage -ImagePath ($isoFolder + "sources\install.wim") | where-object { $_.ImageName -eq $wantedImageName } | Select-Object -ExpandProperty ImageIndex

#Mounting the WIM image
Set-ItemProperty -Path ($isoFolder + "sources\install.wim") -Name IsReadOnly -Value $false
Mount-WindowsImage -ImagePath ($isoFolder + "sources\install.wim") -Path $installImageFolder -Index $wantedImageIndex

#Detecting provisionned app packages
$detectedProvisionnedPackages = Get-AppxProvisionedPackage -Path $installImageFolder

#Removing unwanted provisionned app packages
Foreach ($detectedProvisionnedPackage in $detectedProvisionnedPackages)
{
	Foreach ($unwantedProvisionnedPackage in $unwantedProvisionnedPackages)
	{
		If ($detectedProvisionnedPackage.PackageName.Contains($unwantedProvisionnedPackage))
		{
			Remove-AppxProvisionedPackage -Path $installImageFolder -PackageName $detectedProvisionnedPackage.PackageName -ErrorAction SilentlyContinue
		}
	}
}

#Detecting windows packages
$detectedWindowsPackages = Get-WindowsPackage -Path $installImageFolder

#Removing unwanted windows packages
Foreach ($detectedWindowsPackage in $detectedWindowsPackages)
{
	Foreach ($unwantedWindowsPackage in $unwantedWindowsPackages)
	{
		If ($detectedWindowsPackage.PackageName.Contains($unwantedWindowsPackage))
		{
			Remove-WindowsPackage -Path $installImageFolder -PackageName $detectedWindowsPackage.PackageName -ErrorAction SilentlyContinue
		}
	}
}

#Removing Edge
Remove-Item ($installImageFolder + "Program Files (x86)\Microsoft\Edge") -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item ($installImageFolder + "Program Files (x86)\Microsoft\EdgeUpdate") -Force -Recurse -ErrorAction SilentlyContinue

#Removing OneDrive
takeown /f ($installImageFolder + "Windows\System32\OneDriveSetup.exe")
icacls ($installImageFolder + "Windows\System32\OneDriveSetup.exe") /grant ("$env:username"+":F") /T /C
Remove-Item -Force $($installImageFolder + "Windows\System32\OneDriveSetup.exe")

# Loading the registry from the mounted WIM image
reg load HKLM\installwim_DEFAULT ($installImageFolder + "Windows\System32\config\default")
reg load HKLM\installwim_NTUSER ($installImageFolder + "Users\Default\ntuser.dat")
reg load HKLM\installwim_SOFTWARE ($installImageFolder + "Windows\System32\config\SOFTWARE")
reg load HKLM\installwim_SYSTEM ($installImageFolder + "Windows\System32\config\SYSTEM")

# Applying following registry patches on the system image:
#	Bypassing system requirements
#	Disabling Teams
#	Disabling Sponsored Apps
#	Enabling Local Accounts on OOBE
#	Disabling Reserved Storage
#	Disabling Chat icon
regedit /s ./tools/installwim_patches.reg

# Unloading the registry
reg unload HKLM\installwim_DEFAULT
reg unload HKLM\installwim_NTUSER
reg unload HKLM\installwim_SOFTWARE
reg unload HKLM\installwim_SYSTEM

#Copying the setup config file
[System.IO.File]::Copy((Get-ChildItem .\tools\autounattend.xml).FullName, ($installImageFolder + "Windows\System32\Sysprep\autounattend.xml"), $true)

#Unmount the install.wim image
Dismount-WindowsImage -Path $installImageFolder -Save

#Moving the wanted image index to a new image
Export-WindowsImage -SourceImagePath ($isoFolder + "sources\install.wim") -SourceIndex $wantedImageIndex -DestinationImagePath ($isoFolder + "sources\install_patched.wim") -CompressionType max

#Delete the old install.wim and rename the new one
rm ($isoFolder + "sources\install.wim")
Rename-Item -Path ($isoFolder + "sources\install_patched.wim") -NewName "install.wim"
################# Ending of install.wim patches ##################

################# Beginning of boot.wim patches ##################
Set-ItemProperty -Path ($isoFolder + "sources\boot.wim") -Name IsReadOnly -Value $false
Mount-WindowsImage -ImagePath ($isoFolder + "sources\boot.wim") -Path $bootImageFolder -Index 2

reg load HKLM\bootwim_DEFAULT ($bootImageFolder + "Windows\System32\config\default")
reg load HKLM\bootwim_NTUSER ($bootImageFolder + "Users\Default\ntuser.dat")
reg load HKLM\bootwim_SYSTEM ($bootImageFolder + "Windows\System32\config\SYSTEM")

# Applying following registry patches on the boot image:
#	Bypassing system requirements
regedit /s ./tools/bootwim_patches.reg

reg unload HKLM\bootwim_DEFAULT
reg unload HKLM\bootwim_NTUSER
reg unload HKLM\bootwim_SYSTEM

#Unmount the boot.wim image
Dismount-WindowsImage -Path $bootImageFolder -Save

#Moving the wanted image index to a new image
Export-WindowsImage -SourceImagePath ($isoFolder + "sources\boot.wim") -SourceIndex 2 -DestinationImagePath ($isoFolder + "sources\boot_patched.wim") -CompressionType max

#Delete the old boot.wim and rename the new one
rm ($isoFolder + "sources\boot.wim")
Rename-Item -Path ($isoFolder + "sources\boot_patched.wim") -NewName "boot.wim"
################# Ending of boot.wim patches ##################

#Copying the setup config file to the iso copy folder
[System.IO.File]::Copy((Get-ChildItem .\tools\autounattend.xml).FullName, ($isoFolder + "autounattend.xml"), $true)

#Building the new trimmed and patched iso file
.\tools\oscdimg.exe -m -o -u2 -udfver102 -bootdata:("2#p0,e,b" + $isoFolder + "boot\etfsboot.com#pEF,e,b" + $isoFolder + "efi\microsoft\boot\efisys.bin") $isoFolder c:\tiny11.iso

#Cleaning the folders used during the process
Remove-Item $isoFolder -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item $installImageFolder -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item $bootImageFolder -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item $rootWorkdir -Force -Recurse -ErrorAction SilentlyContinue
