# Nano11Builder

A script to make your OWN Nano 11 image.

This is a script to automate the build of an Nano 11 iso.
The main goal of this is to use only Microsoft utilities like DISM, and nothing external. The only executable included is oscdimg.exe, which is provided in the Windows ADK and it is used to create bootable ISO images. Also included is an unattended answer file, which is used to bypass the MS account on OOBE.

~~As of now, only build 22621.525 (the one that can be downloaded from the Microsoft website), 22621.1265 (the latest public build) and 25300 (latest Insider build as of now) are supported.~~ every build is supported

Instructions:

1. Download an Windows 11 ISO.
2. Mount the downloaded ISO image using Windows Explorer.
3. Run the nano11builder.bat file.
4. Select the drive letter where the image is mounted (only the letter, no colon (:))
5. Select the SKU that you want the image to be based.
6. Sit back and relax :)
7. When the image is completed, you will see it in the folder where the script was extracted, with the name nano11.iso

What is removed:

Everything Nano11 Removes

Known issues:

~~1. Microsoft Teams (personal) and Cortana are still here. If you find a way to remove them before I find one, feel free to help!~~
2. Although Edge is removed, the icon and a ghost of its taskbar pin are still available. Also, there are some remnants in the Settings. But the app in itself is deleted.
4. Only en-us x64 is supported as of now. This can be easily fixable by the end user, just by replacing every instance of en-us with the language needed (like ro-RO and so on), and every x64 instance with arm64.

And that's pretty much it for now!
Thanks for trying it and let me know how you like it!
