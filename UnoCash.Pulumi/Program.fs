﻿module Program

open Pulumi.FSharp.Azure.ApiManagement.Inputs
open Pulumi.FSharp.Azure.ApiManagement
open Pulumi.FSharp.Azure.AppService.Inputs
open Pulumi.FSharp.Azure.AppInsights
open Pulumi.FSharp.Azure.AppService
open Pulumi.Azure.AppService.Inputs
open Pulumi.FSharp.Azure.Storage
open System.Collections.Generic
open Pulumi.FSharp.Azure.Core
open System.Threading.Tasks
open Pulumi.FSharp.Output
open Pulumi.FSharp.Config
open Pulumi.FSharp.Azure
open System.Diagnostics
open System.Threading
open Pulumi.AzureAD
open Pulumi.FSharp
open System.IO
open System
open Pulumi

type ParsedSasToken =
    | Valid of string * DateTime
    | ExpiredOrInvalid
    | Missing

let infra() =
    let group =
        resourceGroup {
            name "unocash"
        }
    
    let storage =
        account {
            name                   "unocashstorage"
            resourceGroup          group.Name
            accountReplicationType "LRS"
            accountTier            "Standard"
        }
        
    let webContainer =
        container {
            name               "unocashweb"
            storageAccountName storage.Name
            resourceName       "$web"
        }
            
    let buildContainer =
        container {
            name               "unocashbuild"
            storageAccountName storage.Name
        }
    
    let functionPlan =
        plan {
            name          "unocashasp"
            resourceGroup group.Name
            kind          "FunctionApp"
            planSku {
                size "Y1"
                tier "Dynamic"
            }
        }

    let apiBlob =
        blob {
            name                 "unocashapi"
            storageAccountName   storage.Name
            storageContainerName buildContainer.Name
            ``type``             "Block"
            StringAsset          (config.["ApiBuild"] |> File.ReadAllText)
        }
    
    let codeBlobUrl =
        secretOutput {
            return! sasToken {
                        account storage
                        blob    apiBlob
                    }
        }

    let appInsights =
        insights {
            name            "unocashai"
            resourceGroup   group.Name
            applicationType "web"
            retentionInDays 90
        }
        
    let apiManagement =
        let templateOutputs =
            templateDeployment {
                name           "unocashapim"
                resourceGroup  group.Name
                templateBody   (File.ReadAllText("ApiManagement.json"))
                deploymentMode "Incremental"
                parameters     [ "apiManagementServiceName", input "unocashapim"
                                 "location"                , io    group.Location ]
            } |>               
            fun at -> at.Outputs
        
        {| Name = output { let! outputs = templateOutputs
                           return outputs.["name"] }
           GatewayUrl = output { let! outputs = templateOutputs
                                 return outputs.["gatewayUrl"] } |}
    
    logger {
        name              "unocashapimlog"
        apiManagementName apiManagement.Name
        resourceGroup     group.Name
        loggerApplicationInsights {
            instrumentationKey appInsights.InstrumentationKey
        }
    } |> ignore
        
    let webContainerUrl =
        output {
            let! accountName = storage.Name
            let! containerName = webContainer.Name
            
            return sprintf "https://%s.blob.core.windows.net/%s" accountName containerName
        }

    let swApi =
        api {
            name              "unocashapimapi"
            resourceName      "staticwebsite"
            resourceGroup     group.Name
            apiManagementName apiManagement.Name
            displayName       "StaticWebsite"
            protocols         [ "http"; "https" ]
            serviceUrl        webContainerUrl
            path              ""
            revision          "1"
        }

    let spaAdApplication =
        Application("unocashspaaadapp",
                    ApplicationArgs(ReplyUrls = inputList [ io apiManagement.GatewayUrl ],
                                    Oauth2AllowImplicitFlow = input true))
    
    let policyBlobUrlWithSas pulumiName policyXml =
        secretOutput {
            let blob =
                blob {
                    name                 ("unocash" + pulumiName + "policyblob")
                    storageAccountName   storage.Name
                    storageContainerName buildContainer.Name
                    StringAsset          policyXml
                    ``type``             "Block"
                }
            
            let! url =
                blob.Url

            let! sas =
                sasToken {
                    account    storage
                    container  buildContainer
                    duration   {
                        From = DateTime.Now
                        To   = DateTime.Now.AddHours(1.)
                    }
                    permission Read
                }

            return url + sas
        }
        
    let sasExpirationOutputName = "SasTokenExpiration"
    let sasTokenOutputName = "SasToken"
    
    let token =
        secretOutput {
            let! previousOutputs =
                StackReference(Deployment.Instance.StackName).Outputs

            let tokenValidity =
                let getTokenIfValid (expirationString : string) =
                    match DateTime.TryParse expirationString with
                    | true, x when x > DateTime.Now -> Valid (
                                                           previousOutputs.[sasTokenOutputName] :?> string,
                                                           x
                                                       )
                    | _                             -> ExpiredOrInvalid
                
                match previousOutputs.TryGetValue sasExpirationOutputName with
                | true, (:? string as exp) -> getTokenIfValid exp
                | _                        -> Missing
            
            return!
                match tokenValidity with
                | Missing
                | ExpiredOrInvalid      -> let expiry = DateTime.Now.AddYears(1)
                                           sasToken {
                                               account    storage
                                               container  webContainer
                                               duration   {
                                                   From = DateTime.Now
                                                   To   = expiry
                                               }
                                               permission Read
                                           } |> (fun x -> x.Apply(fun y -> (y, expiry)))
                 | Valid (sasToken, e ) -> output { return sasToken, e }
        }
    
    let swApiPolicyBlobLink =
        output {
            let! (tokenValue, _) =
                token
                
            let apiPolicyXml =
                let queryString =
                    tokenValue.Substring(1).Split('&') |>
                    Array.map ((fun pair -> pair.Split('=')) >>
                               (fun arr -> (arr.[0], arr.[1]))) |>
                    Map.ofArray
                
                let formatValues =
                    seq {
                        yield Config().Require("WebEndpoint") :> obj
                        
                        for key in ["sv";"sr";"st";"se";"sp";"spr";"sig"] do
                            yield queryString.[key] :> obj
                    } |>
                    Array.ofSeq
                
                String.Format(File.ReadAllText("StaticWebsiteApimApiPolicy.xml"),
                              formatValues)

            return! policyBlobUrlWithSas "mainapi" apiPolicyXml
        }
    
    apiOperation {
        name              "unocashapimindexoperation"
        resourceGroup     group.Name
        apiManagementName apiManagement.Name
        apiName           swApi.Name
        method            "GET"
        operationId       "get-index"
        urlTemplate       "/"
        displayName       "GET index"
    } |> ignore
        
    apiOperation {
        name              "unocashapimoperation"
        resourceGroup     group.Name
        apiManagementName apiManagement.Name
        apiName           swApi.Name
        method            "GET"
        operationId       "get"
        urlTemplate       "/*"     
        displayName       "GET"
    } |> ignore
    
    let blobLink name fileName =
        output {
            let! appId =
                spaAdApplication.ApplicationId
            
            let policy =
                String.Format(File.ReadAllText(fileName),
                              Config.TenantId,
                              appId)
            
            return! policyBlobUrlWithSas name policy
        }
        
    let functionApiPolicyBlobLink =
        blobLink "functionapi" "APIApimApiPolicy.xml"
    
    let swApiGetIndexPolicyBlobLink =
        blobLink "getindex" "StaticWebsiteApimGetIndexOperationPolicy.xml"
    
    let swApiGetPolicyBlobLink =
        blobLink "get" "StaticWebsiteApimGetOperationPolicy.xml"
    
    let swApiPostPolicyBlobLink =
        blobLink "post" "StaticWebsiteApimPostOperationPolicy.xml"
    
    apiOperation {
        name              "unocashapimpostoperation"
        resourceGroup     group.Name
        apiManagementName apiManagement.Name
        apiName           swApi.Name
        method            "POST"
        operationId       "post-aad-token"
        urlTemplate       "/"
        displayName       "POST AAD token"
    } |> ignore
    
    let app =
        functionApp {
            name             "unocashapp"
            resourceGroup    group.Name
            appServicePlanId functionPlan.Id
            appSettings     [
                "runtime"                        , input "dotnet"
                "WEBSITE_RUN_FROM_PACKAGE"       , io codeBlobUrl
                "APPINSIGHTS_INSTRUMENTATIONKEY" , io appInsights.InstrumentationKey
                "StorageAccountConnectionString" , io storage.PrimaryConnectionString
                "FormRecognizerKey"              , input ""
                "FormRecognizerEndpoint"         , input ""
            ]               
            storageAccountName storage.Name
            version            "~3"
            functionAppSiteConfig {
                FunctionAppSiteConfigCorsArgs(AllowedOrigins = inputList [ io apiManagement.GatewayUrl ],
                                              SupportCredentials = input true)
            }
        }
    
    let apiFunction =
        api {
            name              "unocashapimapifunction"
            resourceName      "api"
            path              "api"
            resourceGroup     group.Name
            apiManagementName apiManagement.Name
            displayName       "API"
            protocols         [ "https" ]
            serviceUrl        (app.DefaultHostname.Apply (sprintf "https://%s"))
            path              ""
            revision          "1"
        }
    
    let apiOperation (httpMethod : string) =
        apiOperation {
            name              ("unocashapimapifunction" + (httpMethod.ToString().ToLower()))
            resourceGroup     group.Name
            apiManagementName apiManagement.Name
            apiName           apiFunction.Name
            method            httpMethod
            operationId       (httpMethod.ToLower())
            urlTemplate       "/*"     
            displayName       httpMethod
        }
    
    [ "GET"; "POST"; "DELETE"; "PUT" ] |>
    List.iter (apiOperation >> ignore)
    
    blob {
        name                 "unocashwebconfig"
        resourceName         "apibaseurl"
        storageAccountName   storage.Name
        storageContainerName webContainer.Name
        ``type``             "Block"
        StringAsset          (config.["WebEndpoint"] + "/api")
    } |> ignore

    let sasExpiry =
        output {
            let! (_, expiry) = token            
            return expiry.ToString("u")
        }
    
    dict [
        "Hostname",                           app.DefaultHostname            :> obj
        "ResourceGroup",                      group.Name                     :> obj
        "StorageAccount",                     storage.Name                   :> obj
        "ApiManagementEndpoint",              apiManagement.GatewayUrl       :> obj
        "ApiManagement",                      apiManagement.Name             :> obj
        "StaticWebsiteApi",                   swApi.Name                     :> obj
        "FunctionApi",                        apiFunction.Name               :> obj
        "ApplicationId",                      spaAdApplication.ApplicationId :> obj
        "FunctionName",                       app.Name                       :> obj
        
        // Outputs to read on next deployment to check for changes
        sasTokenOutputName,                   token.Apply fst                :> obj
        sasExpirationOutputName,              sasExpiry                      :> obj
                               
        // API Management policy files URLs                                        
        "StaticWebsiteApiPolicyLink",         swApiPolicyBlobLink            :> obj
        "StaticWebsiteApiPostPolicyLink",     swApiPostPolicyBlobLink        :> obj
        "StaticWebsiteApiGetPolicyLink",      swApiGetPolicyBlobLink         :> obj
        "StaticWebsiteApiGetIndexPolicyLink", swApiGetIndexPolicyBlobLink    :> obj
        "FunctionApiPolicyLink",              functionApiPolicyBlobLink      :> obj
    ]

type bclList<'a> =
    System.Collections.Generic.List<'a>

let ignoreBlobSourceChanges (args : ResourceTransformationArgs) =
    if args.Resource.GetResourceType() = "azure:storage/blob:Blob" then
        args.Options.IgnoreChanges <- bclList(["source"])
    ResourceTransformationResult(args.Args, args.Options) |> Nullable

let stackOptions =
        StackOptions(
            ResourceTransformations =
                bclList([
                    if Environment.GetEnvironmentVariable("AGENT_ID") = null then
                        yield ResourceTransformation(ignoreBlobSourceChanges)
                ]))

[<EntryPoint>]
let main _ =
    let rec waitForDebugger () =
        match Debugger.IsAttached with
        | false -> Thread.Sleep(100)
                   printf "."
                   waitForDebugger ()
        | true  -> printfn " attached"

    match Environment.GetEnvironmentVariable("PULUMI_DEBUG_WAIT") = "1" with
    | true -> printf "Awaiting debugger to attach to the process"
              waitForDebugger ()
    | _    -> ()

    Deployment.RunAsync(Func<Task<IDictionary<string, obj>>>(infra >> Task.FromResult), stackOptions)
              .GetAwaiter()
              .GetResult()