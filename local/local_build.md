
```ps1
.\New-HyperVCloudImageVM.ps1 -VMProcessorCount 16 -VMMemoryStartupBytes 2GB -VMMinimumBytes 500MB -VMMaximumBytes 16GB -VHDSizeBytes 128GB -VMName "openwrt-development" -ImageVersion "22.04" -VMGeneration 2 -KeyboardLayout en -GuestAdminUsername lk -GuestAdminPassword lk233 -VMDynamicMemoryEnabled $true -VirtualSwitchName WAN -Verbose -ImageTypeAzure $true -VMMachine_StoragePath "F:\hyper-v" -ShowSerialConsoleWindow 

-ShowVmConnectWindow 


```
hvc ssh openwrt-development -Z lk233
