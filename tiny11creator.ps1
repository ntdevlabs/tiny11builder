#Defining preferences variables
Write-Output "Loading configuration from config.json..."
$config = (Get-Content "config.json" -Raw) | ConvertFrom-Json
$wantedImageName = $config.WantedWindowsEdition
$unwantedProvisionnedPackages = $config.ProvisionnedPackagesToRemove
$unwantedWindowsPackages = $config.WindowsPackagesToRemove
$pathsToDelete = $config.PathsToDelete

#Defining system variables
Write-Output "Creating needed variables..."
$rootWorkdir = "c:\tiny11\"
$isoFolder = $rootWorkdir + "iso\"
$installImageFolder = $rootWorkdir + "installimage\"
$bootImageFolder = $rootWorkdir + "bootimage\"
$isoPath = "c:\windows11.iso"
$yes = ("O" | choice)[1] #this variable gets the "y" from "yes" (or any other letter it the "yes" word doesn't start with "y" in your language)
						 #it is used to answer automatically to the takeown.exe command

#Downloading the Windows 11 ISO using WindowsIsoDownloader
#Write-Output "Downloading Windows 11 iso file from Microsoft using WindowsIsoDownloader..."
#Start-Process ./tools/WindowsIsoDownloader/WindowsIsoDownloader.exe -NoNewWindow -Wait

#Mount the Windows 11 ISO
Write-Output "Mounting the original iso..."
$mountResult = Mount-DiskImage -ImagePath $isoPath
$isoDriveLetter = ($mountResult | Get-Volume).DriveLetter

#Creating needed temporary folders
Write-Output "Creating temporary folders..."
md $rootWorkdir | Out-Null
md $isoFolder | Out-Null
md $installImageFolder | Out-Null
md $bootImageFolder | Out-Null

#Copying the ISO files to the ISO folder
Write-Output "Copying the content of the original iso to the work folder..."
cp -Recurse ($isoDriveLetter + ":\*") $isoFolder | Out-Null

#Unmounting the original ISO since we don't need it anymore (we have a copy of the content)
Write-Output "Unmounting the original iso..."
Dismount-DiskImage -ImagePath $isoPath | Out-Null

################# Beginning of install.wim patches ##################
#Getting the wanted image index
$wantedImageIndex = Get-WindowsImage -ImagePath ($isoFolder + "sources\install.wim") | where-object { $_.ImageName -eq $wantedImageName } | Select-Object -ExpandProperty ImageIndex

#Mounting the WIM image
Write-Output "Mounting the install.wim image..."
Set-ItemProperty -Path ($isoFolder + "sources\install.wim") -Name IsReadOnly -Value $false | Out-Null
Mount-WindowsImage -ImagePath ($isoFolder + "sources\install.wim") -Path $installImageFolder -Index $wantedImageIndex | Out-Null

#Detecting provisionned app packages
Write-Output "Removing unwanted app packages from the install.wim image..."
$detectedProvisionnedPackages = Get-AppxProvisionedPackage -Path $installImageFolder

#Removing unwanted provisionned app packages
Foreach ($detectedProvisionnedPackage in $detectedProvisionnedPackages)
{
	Foreach ($unwantedProvisionnedPackage in $unwantedProvisionnedPackages)
	{
		If ($detectedProvisionnedPackage.PackageName.Contains($unwantedProvisionnedPackage))
		{
			Remove-AppxProvisionedPackage -Path $installImageFolder -PackageName $detectedProvisionnedPackage.PackageName -ErrorAction SilentlyContinue | Out-Null
		}
	}
}

#Detecting windows packages
Write-Output "Removing unwanted windows packages from the install.wim image..."
$detectedWindowsPackages = Get-WindowsPackage -Path $installImageFolder

#Removing unwanted windows packages
Foreach ($detectedWindowsPackage in $detectedWindowsPackages)
{
	Foreach ($unwantedWindowsPackage in $unwantedWindowsPackages)
	{
		If ($detectedWindowsPackage.PackageName.Contains($unwantedWindowsPackage))
		{
			Remove-WindowsPackage -Path $installImageFolder -PackageName $detectedWindowsPackage.PackageName -ErrorAction SilentlyContinue | Out-Null
		}
	}
}

Write-Output "Deleting PathsToDelete from the install.wim image..."
Foreach ($pathToDelete in $pathsToDelete)
{
	$fullpath = ($installImageFolder + $pathToDelete.Path)

	If ($pathToDelete.IsFolder -eq $true)
	{
		takeown /f $fullpath /r /d $yes | Out-Null
		icacls $fullpath /grant ("$env:username"+":F") /T /C | Out-Null
		Remove-Item -Force $fullpath -Recurse -ErrorAction SilentlyContinue | Out-Null
	}
	Else
	{
		takeown /f $fullpath | Out-Null
		icacls $fullpath /grant ("$env:username"+":F") /T /C | Out-Null
		Remove-Item -Force $fullpath -ErrorAction SilentlyContinue | Out-Null
	}
}

# Loading the registry from the mounted WIM image
Write-Output "Patching the registry in the install.wim image..."
reg load HKLM\installwim_DEFAULT ($installImageFolder + "Windows\System32\config\default") | Out-Null
reg load HKLM\installwim_NTUSER ($installImageFolder + "Users\Default\ntuser.dat") | Out-Null
reg load HKLM\installwim_SOFTWARE ($installImageFolder + "Windows\System32\config\SOFTWARE") | Out-Null
reg load HKLM\installwim_SYSTEM ($installImageFolder + "Windows\System32\config\SYSTEM") | Out-Null

# Applying following registry patches on the system image:
#	Bypassing system requirements
#	Disabling Teams
#	Disabling Sponsored Apps
#	Enabling Local Accounts on OOBE
#	Disabling Reserved Storage
#	Disabling Chat icon
regedit /s ./tools/installwim_patches.reg | Out-Null

# Unloading the registry
reg unload HKLM\installwim_DEFAULT | Out-Null
reg unload HKLM\installwim_NTUSER | Out-Null
reg unload HKLM\installwim_SOFTWARE | Out-Null
reg unload HKLM\installwim_SYSTEM | Out-Null

#Copying the setup config file
Write-Output "Placing the autounattend.xml file in the install.wim image..."
[System.IO.File]::Copy((Get-ChildItem .\tools\autounattend.xml).FullName, ($installImageFolder + "Windows\System32\Sysprep\autounattend.xml"), $true) | Out-Null

#Unmount the install.wim image
Write-Output "Unmounting the install.wim image..."
Dismount-WindowsImage -Path $installImageFolder -Save | Out-Null

#Moving the wanted image index to a new image
Write-Output "Creating a clean install.wim image without all unecessary indexes..."
Export-WindowsImage -SourceImagePath ($isoFolder + "sources\install.wim") -SourceIndex $wantedImageIndex -DestinationImagePath ($isoFolder + "sources\install_patched.wim") -CompressionType max | Out-Null

#Delete the old install.wim and rename the new one
rm ($isoFolder + "sources\install.wim") | Out-Null
Rename-Item -Path ($isoFolder + "sources\install_patched.wim") -NewName "install.wim" | Out-Null
################# Ending of install.wim patches ##################

################# Beginning of boot.wim patches ##################
Set-ItemProperty -Path ($isoFolder + "sources\boot.wim") -Name IsReadOnly -Value $false | Out-Null
Write-Output "Mounting the boot.wim image..."
Mount-WindowsImage -ImagePath ($isoFolder + "sources\boot.wim") -Path $bootImageFolder -Index 2 | Out-Null

Write-Output "Patching the registry in the boot.wim image..."
reg load HKLM\bootwim_DEFAULT ($bootImageFolder + "Windows\System32\config\default") | Out-Null
reg load HKLM\bootwim_NTUSER ($bootImageFolder + "Users\Default\ntuser.dat") | Out-Null
reg load HKLM\bootwim_SYSTEM ($bootImageFolder + "Windows\System32\config\SYSTEM") | Out-Null

# Applying following registry patches on the boot image:
#	Bypassing system requirements
regedit /s ./tools/bootwim_patches.reg | Out-Null

reg unload HKLM\bootwim_DEFAULT | Out-Null
reg unload HKLM\bootwim_NTUSER | Out-Null
reg unload HKLM\bootwim_SYSTEM | Out-Null

#Unmount the boot.wim image
Write-Output "Unmounting the boot.wim image..."
Dismount-WindowsImage -Path $bootImageFolder -Save | Out-Null

#Moving the wanted image index to a new image
Write-Output "Creating a clean boot.wim image without all unecessary indexes..."
Export-WindowsImage -SourceImagePath ($isoFolder + "sources\boot.wim") -SourceIndex 2 -DestinationImagePath ($isoFolder + "sources\boot_patched.wim") -CompressionType max | Out-Null

#Delete the old boot.wim and rename the new one
rm ($isoFolder + "sources\boot.wim") | Out-Null
Rename-Item -Path ($isoFolder + "sources\boot_patched.wim") -NewName "boot.wim" | Out-Null
################# Ending of boot.wim patches ##################

#Copying the setup config file to the iso copy folder
[System.IO.File]::Copy((Get-ChildItem .\tools\autounattend.xml).FullName, ($isoFolder + "autounattend.xml"), $true) | Out-Null

#Building the new trimmed and patched iso file
Write-Output "Building the tiny11.iso file..."
.\tools\oscdimg.exe -m -o -u2 -udfver102 -bootdata:("2#p0,e,b" + $isoFolder + "boot\etfsboot.com#pEF,e,b" + $isoFolder + "efi\microsoft\boot\efisys.bin") $isoFolder c:\tiny11.iso | Out-Null

#Cleaning the folders used during the process
Write-Output "Removing work folders..."
Remove-Item $isoFolder -Force -Recurse -ErrorAction SilentlyContinue | Out-Null
Remove-Item $installImageFolder -Force -Recurse -ErrorAction SilentlyContinue | Out-Null
Remove-Item $bootImageFolder -Force -Recurse -ErrorAction SilentlyContinue | Out-Null
Remove-Item $rootWorkdir -Force -Recurse -ErrorAction SilentlyContinue | Out-Null
