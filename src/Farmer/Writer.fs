module Farmer.Writer

open Farmer.Internal

module Outputters =
    let storageAccount (resource:StorageAccount) = {|
        ``type`` = "Microsoft.Storage/storageAccounts"
        sku = {| name = resource.Sku |}
        kind = "StorageV2"
        name = resource.Name.Value
        apiVersion = "2018-07-01"
        location = resource.Location
    |}

    let appInsights (resource:AppInsights) =
        let (ResourceName linkedWebsite) = resource.LinkedWebsite
        {| ``type`` = "Microsoft.Insights/components"
           kind = "web"
           name = resource.Name.Value
           location = resource.Location
           apiVersion = "2014-04-01"
           tags =
               [ sprintf "[concat('hidden-link:', resourceGroup().id, '/providers/Microsoft.Web/sites/', '%s')]" linkedWebsite, "Resource"
                 "displayName", "AppInsightsComponent" ] |> Map.ofList
           properties =
               {| name = resource.Name.Value
                  Application_Type = "web"
                  ApplicationId = linkedWebsite |}
        |}
    let serverFarm (farm:ServerFarm) = {|
        ``type`` = "Microsoft.Web/serverfarms"
        sku =
            let baseProps =
                {| name = farm.Sku
                   tier = farm.Tier
                   size = farm.WorkerSize |}
            if farm.IsDynamic then box {| baseProps with family = "Y"; capacity = 0 |}
            else box {| baseProps with numberOfWorkers = farm.WorkerCount |}
        name = farm.Name.Value
        apiVersion = "2016-09-01"
        location = farm.Location
        properties =
            if farm.IsDynamic then
                box {| name = farm.Name.Value; computeMode = "Dynamic" |}
            else
                box {| name = farm.Name.Value
                       perSiteScaling = false
                       reserved = false |}
    |}
    let webApp (webApp:WebApp) =
        let baseProps = {|
            ``type`` = "Microsoft.Web/sites"
            name = webApp.Name.Value
            apiVersion = "2016-08-01"
            location = webApp.Location
            dependsOn = webApp.Dependencies |> List.map(fun p -> p.Value)
            resources =
                webApp.Extensions
                |> Set.toList
                |> List.map (function
                | AppInsightsExtension ->
                    {| apiVersion = "2016-08-01"
                       name = "Microsoft.ApplicationInsights.AzureWebSites"
                       ``type`` = "siteextensions"
                       dependsOn = [ webApp.Name.Value ]
                       properties = {||}
                    |})
            properties =
                {| serverFarmId = webApp.ServerFarm.Value
                   siteConfig =
                    {| appSettings =
                        webApp.AppSettings
                        |> List.map(fun (k,v) -> {| name = k; value = v |})
                    |}
                |}
        |}
        match webApp.Kind with
        | Some kind -> box {| baseProps with kind = kind |}
        | None -> box baseProps

    let cosmosDbContainer (container:CosmosDbContainer) = {|
        ``type`` = "Microsoft.DocumentDb/databaseAccounts/apis/databases/containers"
        name = sprintf "%s/sql/%s/%s" container.Account.Value container.Database.Value container.Name.Value
        apiVersion = "2016-03-31"
        dependsOn = [ container.Database.Value ]
        properties =
            {| resource =
                {| id = container.Name.Value
                   partitionKey =
                    {| paths = container.PartitionKey.Paths
                       kind = string container.PartitionKey.Kind |}
                   indexingPolicy =
                    {| indexingMode = "consistent"
                       includedPaths =
                           container.IndexingPolicy.IncludedPaths
                           |> List.map(fun p ->
                            {| path = p.Path
                               indexes =
                                p.Indexes
                                |> List.map(fun i ->
                                    {| kind = string i.Kind
                                       dataType = (string i.DataType).ToLower()
                                       precision = -1 |})
                            |})
                       excludedPaths =
                        container.IndexingPolicy.ExcludedPaths
                        |> List.map(fun p -> {| path = p |})
                    |}
                |}
            |}
    |}
    let cosmosDbServer (cosmosDb:CosmosDbAccount) = {|
        ``type`` = "Microsoft.DocumentDB/databaseAccounts"
        name = cosmosDb.Name.Value
        apiVersion = "2016-03-31"
        location = cosmosDb.Location
        kind = "GlobalDocumentDB"
        properties =
            let baseProps =
                let consistencyPolicy =
                    match cosmosDb.ConsistencyPolicy with
                    | BoundedStaleness(maxStaleness, maxInterval) ->
                        box {| defaultConsistencyLevel = "BoundedStaleness"
                               maxStalenessPrefix = maxStaleness
                               maxIntervalInSeconds = maxInterval |}
                    | Session
                    | Eventual
                    | ConsistentPrefix
                    | Strong ->
                        box {| defaultConsistencyLevel = string cosmosDb.ConsistencyPolicy |}
                {| consistencyPolicy = consistencyPolicy
                   databaseAccountOfferType = "Standard" |}
            match cosmosDb.WriteModel with
            | AutoFailover secondary ->
                {| baseProps with
                      enableAutomaticFailover = true
                      locations = [
                        {| locationName = cosmosDb.Location; failoverPriority = 0 |}
                        {| locationName = secondary; failoverPriority = 1 |}
                      ]
                |} |> box
            | MultiMaster secondary ->
                {| baseProps with
                      autoenableMultipleWriteLocations = true
                      locations = [
                        {| locationName = cosmosDb.Location; failoverPriority = 0 |}
                        {| locationName = secondary; failoverPriority = 1 |}
                      ]
                |} |> box
            | NoFailover ->
                box baseProps
    |}
    let cosmosDbSql (cosmosDbSql:CosmosDbSql) = {|
        ``type`` = "Microsoft.DocumentDB/databaseAccounts/apis/databases"
        name = sprintf "%s/sql/%s" cosmosDbSql.Account.Value cosmosDbSql.Name.Value
        apiVersion = "2016-03-31"
        dependsOn = [ cosmosDbSql.Account.Value ]
        properties =
            {| resource = {| id = cosmosDbSql.Name.Value |}
               options = {| throughput = cosmosDbSql.Throughput |} |}
    |}

    let sqlAzure (database:SqlAzure) = {|
        ``type`` = "Microsoft.Sql/servers"
        name = database.ServerName.Value
        apiVersion = "2014-04-01-preview"
        location = database.Location
        tags = {| displayName = "SqlServer" |}
        properties =
            let (SecureParameter passwordParam) = database.AdministratorLoginPassword
            {| administratorLogin = database.AdministratorLogin
               administratorLoginPassword = sprintf "[parameters('%s')]" passwordParam
               version = "12.0" |}
        resources = [
            yield
                {| ``type`` = "databases"
                   name = database.DbName.Value               
                   apiVersion = "2015-01-01"
                   location = database.Location
                   tags = {| displayName = "Database" |}
                   properties =
                    {| edition = database.DbEdition
                       collation = database.DbCollation
                       requestedServiceObjectiveName = database.DbObjective |}
                   dependsOn = [
                       database.ServerName.Value
                   ]
                   resources = [
                       {| ``type`` = "transparentDataEncryption"
                          comments = "Transparent Data Encryption"
                          name = "current"                      
                          apiVersion = "2014-04-01-preview"
                          properties = {| status = string database.TransparentDataEncryption |}
                          dependsOn = [ database.DbName.Value ]
                       |}
                   ]
                |} |> box
            yield!
                database.FirewallRules
                |> List.map(fun fw ->
                    {| ``type`` = "firewallrules"
                       name = fw.Name
                       apiVersion = "2014-04-01"
                       location = database.Location
                       properties =
                        {| endIpAddress = string fw.Start
                           startIpAddress = string fw.End |}
                       dependsOn = [ database.ServerName.Value ]
                    |} |> box)
        ]

    |}

let processTemplate (template:ArmTemplate) = {|
    ``$schema`` = "https://schema.management.azure.com/schemas/2015-01-01/deploymentTemplate.json#"
    contentVersion = "1.0.0.0"
    resources =
        template.Resources
        |> List.map(function
            | AppInsights ai -> Outputters.appInsights ai |> box
            | StorageAccount s -> Outputters.storageAccount s |> box
            | ServerFarm s -> Outputters.serverFarm s |> box
            | WebApp wa -> Outputters.webApp wa |> box
            | CosmosAccount cds -> Outputters.cosmosDbServer cds |> box
            | CosmosSqlDb db -> Outputters.cosmosDbSql db |> box
            | CosmosContainer c -> Outputters.cosmosDbContainer c |> box
            | SqlServer sql -> Outputters.sqlAzure sql |> box)
    parameters = [
        for resource in template.Resources do
            match resource with
            | SqlServer sql ->
                let (SecureParameter p) = sql.AdministratorLoginPassword
                yield p, {| ``type`` = "securestring" |}
            | _ ->
                ()            
        ] |> Map.ofList
    outputs =
        template.Outputs
        |> List.map(fun (k, v) ->
            k, Map [ "type", "string"
                     "value", v ])
        |> Map
|}

let toJson = processTemplate >> Newtonsoft.Json.JsonConvert.SerializeObject
let toFile f c = System.IO.File.WriteAllText(f, c)