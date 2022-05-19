# UMDReplaceK

Tiny tool to replace data files in a PSP UMD ISO or PS2 ISO.

Sample usage follows as such

### Running in Batch Mode
If you plan to run this in a "batch" mode where you passing in a batch file that contains the file name arguments for replacement see below. 

Sample input should be the following schema:
`UMDReplaceK.exe <path to ISO file that you want to patch> <path to text file with batch parameters>`

I have included a sample batch file of what we're using for the Kokoro Connect VN Translation Project in the various releases for you to follow as a template. The input parameters in the file will look like this though:

> file path within the iso that you want to replace
> the new file path that want to insert
> file path within the iso that you want to replace
> the new file path that want to insert
> ...

### Running in Argument List Mode
If you want to simply pass in the arguments rather than storing them in a temporary (or permanent) batch file, then your sample input will follow this schema instead:
`UMDReplaceK.exe <path to ISO file that you want to patch> <file path within the iso that you want to replace> <the new file path that want to insert>`

### Credits

Credit goes to @Dormanil for helping out and for helping maintain my sanity while I worked on this.
Credit to the Kokoro Connect Fan Translation Project for bringing to light the necessity of the creation of this tool.
Credit goes to IlDucci for pointing out this tool could be updated to also support PS2 Isos as well as PSP Isos.
Credit to CUE for their original version of UMDReplace which I used as the very beginning starting blocks of this tool before it evolved and became so very much more.


### Contact
If you encounter issues or would like to provide feedback regarding this tool, you can contact me via Discord spudmantwo#3039 or via email SpudManTwo@gmail.com . Alternatively, you can create an issue here on github and I will see it.
