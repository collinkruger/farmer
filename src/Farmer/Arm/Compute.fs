[<AutoOpen>]
module Farmer.Arm.Compute

open Farmer
open Farmer.Identity
open Farmer.Vm
open System
open System.Text

let virtualMachines = ResourceType ("Microsoft.Compute/virtualMachines", "2018-10-01")
let extensions = ResourceType ("Microsoft.Compute/virtualMachines/extensions", "2019-12-01")

type CustomScriptExtension =
    { Name : ResourceName
      Location : Location
      VirtualMachine : ResourceName
      FileUris : Uri list
      ScriptContents : string
      OS : OS
      Tags: Map<string,string>  }
    interface IArmResource with
        member this.ResourceId = extensions.resourceId (this.VirtualMachine/this.Name)
        member this.JsonModel =
            {| extensions.Create(this.VirtualMachine/this.Name, this.Location, [ virtualMachines.resourceId this.VirtualMachine ], this.Tags) with
                   properties =
                    match this.OS with
                    | Windows ->
                        {| publisher = "Microsoft.Compute"
                           ``type`` = "CustomScriptExtension"
                           typeHandlerVersion = "1.10"
                           autoUpgradeMinorVersion = true
                           settings = {| fileUris = this.FileUris |> List.map string |}
                           protectedSettings = {| commandToExecute = this.ScriptContents |}
                        |} |> box
                    | Linux ->
                        {| publisher = "Microsoft.Azure.Extensions"
                           ``type`` = "CustomScript"
                           typeHandlerVersion = "2.1"
                           autoUpgradeMinorVersion = true
                           protectedSettings =
                            {| fileUris =
                                this.FileUris
                                |> List.map string
                               script =
                                this.ScriptContents
                                |> Encoding.UTF8.GetBytes
                                |> Convert.ToBase64String |}
                        |} :> _
            |} :> _

type VirtualMachine =
    { Name : ResourceName
      Location : Location
      Zone: int option
      StorageAccount : ResourceName option
      Size : VMSize
      Credentials : {| Username : string; Password : SecureParameter |}
      CustomData : string option
      DisablePasswordAuthentication: bool option
      PublicKeys : (string  * string ) list option
      Image : ImageDefinition
      OsDisk : DiskInfo
      DataDisks : DiskInfo list
      NetworkInterfaceName : ResourceName
      Identity : Identity.ManagedIdentity
      Tags: Map<string,string>  }
    interface IParameters with
        member this.SecureParameters =
            if this.DisablePasswordAuthentication.IsSome && this.DisablePasswordAuthentication.Value then
                []
            else
                [ this.Credentials.Password ]
    interface IArmResource with
        member this.ResourceId = virtualMachines.resourceId this.Name
        member this.JsonModel =
            let dependsOn = [
                networkInterfaces.resourceId this.NetworkInterfaceName
                yield! this.StorageAccount |> Option.mapList storageAccounts.resourceId
            ]
            {| virtualMachines.Create(this.Name, this.Location, dependsOn, this.Tags) with
                identity =
                    if this.Identity = ManagedIdentity.Empty then Unchecked.defaultof<_>
                    else this.Identity.ToArmJson
                properties =
                 {| hardwareProfile = {| vmSize = this.Size.ArmValue |}
                    osProfile =
                     {| computerName = this.Name.Value
                        adminUsername = this.Credentials.Username
                        adminPassword =
                            if this.DisablePasswordAuthentication.IsSome && this.DisablePasswordAuthentication.Value then //If the disablePasswordAuthentication is set and the value is true then we don't need a password
                                null
                            else
                                this.Credentials.Password.ArmExpression.Eval()
                        customData = this.CustomData |> Option.map (System.Text.Encoding.UTF8.GetBytes >> Convert.ToBase64String) |> Option.toObj
                        linuxConfiguration =
                            if this.DisablePasswordAuthentication.IsSome || this.PublicKeys.IsSome then
                                {|
                                    disablePasswordAuthentication = this.DisablePasswordAuthentication |> Option.map box |> Option.toObj
                                    ssh = match this.PublicKeys with
                                          | Some publicKeys ->
                                            {|
                                                publicKeys = publicKeys |> List.map (fun k -> {| path = fst k;keyData = snd k|})
                                            |}
                                          | None -> Unchecked.defaultof<_>
                                |}
                            else
                                Unchecked.defaultof<_>
                        |}
                    storageProfile =
                        let vmNameLowerCase = this.Name.Value.ToLower()
                        {| imageReference =
                            {| publisher = this.Image.Publisher.ArmValue
                               offer = this.Image.Offer.ArmValue
                               sku = this.Image.Sku.ArmValue
                               version = "latest" |}
                           osDisk =
                            {| createOption = "FromImage"
                               name = $"{vmNameLowerCase}-osdisk"
                               diskSizeGB = this.OsDisk.Size
                               managedDisk = {| storageAccountType = this.OsDisk.DiskType.ArmValue |}
                            |}
                           dataDisks =
                            this.DataDisks
                            |> List.mapi(fun lun dataDisk ->
                                {| createOption = "Empty"
                                   name = $"{vmNameLowerCase}-datadisk-{lun}"
                                   diskSizeGB = dataDisk.Size
                                   lun = lun
                                   managedDisk = {| storageAccountType = dataDisk.DiskType.ArmValue |} |})
                        |}
                    networkProfile =
                        {| networkInterfaces = [
                            {| id = networkInterfaces.resourceId(this.NetworkInterfaceName).Eval() |}
                           ]
                        |}
                    diagnosticsProfile =
                        match this.StorageAccount with
                        | Some storageAccount ->
                            box
                                {| bootDiagnostics =
                                    {| enabled = true
                                       storageUri = $"[reference('{storageAccount.Value}').primaryEndpoints.blob]"
                                    |}
                                |}
                        | None ->
                            box {| bootDiagnostics = {| enabled = false |} |}
                |}
                zones =
                    match this.Zone with
                    | Some zone -> [zone.ToString()]
                    | None -> Unchecked.defaultof<_>
            |} :> _