﻿// ----------------------------------------------------------------------------
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.
// ----------------------------------------------------------------------------
namespace Yaaf.Xmpp.Server

open Yaaf.FSharp.Control
open System.Collections.Concurrent
open Yaaf.Helper
open Yaaf.Logging
open Yaaf.Helper
open Yaaf.Xmpp
open Yaaf.Xmpp.Runtime
open Yaaf.Logging.AsyncTracing

type KeyValuePair<'k, 'v> = System.Collections.Generic.KeyValuePair<'k, 'v>


/// When editing this class be carefull with the XmppClient members, because they often can't be used directly
/// because this would result in a deadlock
type ConnectionManager(myDomain : string) as this = 
    let preNegJid = JabberId.Parse("__pre-negotiation__@" + myDomain)
    let s2sConnections = new ConcurrentDictionary<string, IXmppClient>()
    let clientConnections = new ConcurrentDictionary<string, ConcurrentDictionary<string, IXmppClient>>()
    let pendingConnections = new ConcurrentDictionary<IXmppClient, IXmppClient>()
    let errors = Event<System.EventHandler<exn>, _>()
    let clientNegotiated = Event<System.EventHandler<IXmppClient>, _>()
    let clientDisconnected = Event<System.EventHandler<IXmppClient * exn option>, _>()
    let stopToken = new System.Threading.CancellationTokenSource()
    let closeOldWithConflict (old:IXmppClient) (client:IXmppClient) =
        old.CloseConnection(true, StreamError.create XmlStreamError.Conflict)
            .ContinueWith(fun (t:System.Threading.Tasks.Task<_>) -> 
                if t.IsFaulted then Log.Err (fun () -> 
                    L "Error while closing an old %s collection: %O" client.RemoteJid.FullId t.Exception))
            |> ignore
        Log.Warn (fun () -> L "Replacing connection for %s with a new one!" client.RemoteJid.FullId)
        client
    let filterConnections filter =
        //let id_inc = "incomming"
        let id_server = "server"
        let id_client = "client"
        let servers = s2sConnections.Values |> Seq.map (fun c -> c, id_server) 
        //let out = outgoingS2sConnections.Values |> Seq.map (fun c -> c, id_out)
        let clients = clientConnections.Values |> Seq.collect(fun v -> v.Values) |> Seq.map (fun c -> c, id_client)
        let rec buildFilter filter =
           match filter with
           | IsServer -> (fun (c:IXmppClient, t) -> t = id_server)
           //| IsIncommingServer -> (fun (c, t) -> t = id_server) 
           | IsClient -> (fun (c, t) -> t = id_client && c.StreamType.OnClientStream)
           | IsComponent -> (fun (c, t) -> (t = id_server) && c.StreamType.OnComponentStream)
           | Advanced f -> (fun (c, _) -> f c)
           | Not (inner) -> (fun arg -> not <| buildFilter inner arg)
           | And (left, right) -> (fun arg -> buildFilter left arg && buildFilter right arg)
           | Or (left, right) -> (fun arg -> buildFilter left arg || buildFilter right arg)
        servers
          //|> Seq.append out
          |> Seq.append clients
          |> Seq.filter (buildFilter filter)
          |> Seq.map fst
          |> Seq.toList
          |> Seq.ofList

    let addClientConnection (jid : JabberId) (xmppClient : IXmppClient) = 
        // Note don't use xmppClient as we are within a context (possibly within the NegotiationComplete event)
        let bareId = jid.BareId
        let resources = clientConnections.GetOrAdd(bareId, fun _ -> new ConcurrentDictionary<_, _>())
        match jid.Resource with
        | None -> failwith "no resource attached to client"
        | Some r -> 
            resources.AddOrUpdate(r, xmppClient, fun _ old -> closeOldWithConflict old xmppClient) |> ignore
            Log.Info(fun () -> L "Client connected and added to connections: %s" jid.FullId)
    
    let addServerConnection (jid : JabberId) (xmppClient : IXmppClient) = 
        let bareId = jid.BareId
        s2sConnections.AddOrUpdate(bareId, xmppClient, fun _ c -> closeOldWithConflict c xmppClient) |> ignore
    
    let addConnection (streamType : StreamType) (jid : JabberId) (xmppClient : IXmppClient)  = 
        (if streamType.OnClientStream then addClientConnection
         else addServerConnection) jid xmppClient

        async {
            clientNegotiated.Trigger(this, (xmppClient))
        } |> Log.TraceMe |> Async.Start
    [<CLIEventAttribute>]
    member __.Errors = errors.Publish
    
    [<CLIEventAttribute>]
    member __.ClientNegotiated = clientNegotiated.Publish
    
    [<CLIEventAttribute>]
    member __.ClientDisconnected = clientDisconnected.Publish
    
    // This member should be protected
    member __.ClientExited((xmppClient : IXmppClient), (res : exn option)) =
        // remove faulted/closed connection
        async { 
            let connectTask = xmppClient.ConnectTask
            let jid = 
                if connectTask.IsCompleted then
                    connectTask.Result
                else preNegJid
            Log.Info(fun () -> 
                L "Client (%s) exited, reason: %s" jid.FullId (match res with
                                                               | None -> "normal exit"
                                                               | Some exn -> exn.Message))
            match res with
            | Some e -> errors.Trigger(this, e)
            | None -> ()
            match pendingConnections.TryRemove(xmppClient) with
            | true, _ ->
                // failed within negotiation/connection establishment
                Log.Warn(fun () -> L "client failed within negotiation, closing connection, reason: %O" res)
            | _ ->
                if not connectTask.IsCompleted then
                    Log.Err(fun () -> L "connected client should have the connectTask completed")
                    //Log.Err(fun () -> L "Unable to resolve RemoteJid of negotiated Client?! Try again!")
                    //do! Async.Sleep 1000
                    //return! x.ClientExited (streamType, (xmppClient), res)
                else 
                    let jid = connectTask.Result
                    let bareId = jid.BareId
                    if xmppClient.StreamType.OnClientStream then 
                        let resources = clientConnections.GetOrAdd(bareId, fun _ -> new ConcurrentDictionary<_, _>())
                        match resources.TryRemove(jid.Resource.Value, xmppClient) with
                        | true -> 
                            Log.Info(fun () -> L "removed client connection")
                            clientDisconnected.Trigger(this, (xmppClient, res))
                        | _ -> ()
                    else 
                        match s2sConnections.TryRemove(bareId, xmppClient) with
                        | true -> 
                            Log.Info(fun () -> L "removed s2s or component connection")
                            clientDisconnected.Trigger(this, (xmppClient, res))
                        | _ -> ()
            xmppClient.Dispose()
            Log.Info(fun () -> L "Disposed connection!")
        }
        |> Log.TraceMe
    
    member private x.MyClientExited (xmppClient : IXmppClient) (res : exn option) = 
        x.ClientExited(xmppClient, res) |> Task.startTask x errors
    
    member private x.MyNegotiationComplete (jid : JabberId) (xmppClient : IXmppClient) = 
        Log.Info(fun () -> L "MyNegotiationComplete")
        match pendingConnections.TryRemove(xmppClient) with
        | true, _ ->
            // negotiation completed
            try 
                if stopToken.Token.IsCancellationRequested then failwith "Server already shut down!"
                addConnection xmppClient.StreamType jid xmppClient
            with exn -> 
                Log.Err(fun () -> sprintf "Error on adding client: %O" exn)
                async { 
                    try 
                        errors.Trigger(x, exn)
                        clientDisconnected.Trigger(x, (xmppClient, Some exn))
                    with exn -> Log.Crit(fun _ -> L "Error while triggering error events: %O" exn)
                }
                |> Log.TraceMe
                |> Async.Start
                xmppClient.CloseConnection true |> ignore
        | _ -> ()
    
    member x.RegisterIncommingConnection(xmppClient : IXmppClient) = 
        if stopToken.Token.IsCancellationRequested then failwith "Server already shut down!"
        pendingConnections.AddOrUpdate(xmppClient, xmppClient, fun _ _ ->
          failwithf "this instance is already pending? Cannot call RegisterIncommingConnection twice!") |> ignore

        xmppClient.Exited.ContinueWith(fun (t:Task<_>) -> x.MyClientExited xmppClient t.Result) |> ignore
        
        async { 
            Log.Verb(fun () -> L "RegisterIncommingConnection")
            let bindComplete (jid : JabberId) = 
                x.MyNegotiationComplete jid xmppClient
            
            xmppClient.ConnectTask.ContinueWith(fun (t:Task<_>) -> bindComplete t.Result) |> ignore

            // Timeout connection after 5 minutes
            do! Async.Sleep(60000 * 5)
            match pendingConnections.TryRemove(xmppClient) with
            | true, _ -> 
                // timeout, CloseConnection should make sure that exited is called
                Log.Warn(fun () -> L "Connection timed out without negotiationComplete event!")
                do! xmppClient.CloseConnection true
            | _ -> ()
        }
        |> Log.TraceMe
        |> Task.startTask x errors
    
    member __.GetConnections(jid : JabberId) =
        let isLocal = jid.Domainpart = myDomain
        let bareId = jid.BareId
        let results =
            if isLocal then 
                let resources = clientConnections.GetOrAdd(bareId, fun _ -> new ConcurrentDictionary<_, _>())
                // Only consider completely negotiated connections (others could deadlock or take a long time to be available)
                let cons = 
                    resources
                    |> Seq.map (fun k -> k.Value)
                    |> Seq.filter (fun k -> k.NegotiationCompleted)
                    |> Seq.toList
                if (jid.Resource.IsNone) then cons
                else cons |> List.filter (fun con -> con.RemoteJid.Resource.Value = jid.Resource.Value)
            else 
                //failwith "outgoing currently not supported"
                match s2sConnections.TryGetValue(bareId) with
                | true, s when s.NegotiationCompleted -> [ s ]
                | _ -> []
        Log.Verb(fun () -> L "GetConnections of %A (isLocal: %O). found %A." bareId isLocal (results |> Seq.map (fun c -> c.RemoteJid)))
        results
    
    member __.Shutdown(force) =
        async {
            stopToken.Cancel()
            let getCloseTask (k : KeyValuePair<_, IXmppClient>) = 
                async {
                    let client = k.Value
                    let name =
                        if client.ConnectTask.IsCompleted then client.ConnectTask.Result
                        else preNegJid
                    Log.Info(fun _ -> L "closing connection for: %s" name.FullId)
                    do! k.Value.CloseConnection force |> Task.await
                    Log.Info(fun _ -> L "CloseConnection finished for %s, waiting for Exited task." name.FullId)
                    do! k.Value.Exited |> Task.awaitNoExnIgnore
                    Log.Info(fun _ -> L "%s successfully finished!" name.FullId)
                } |> Log.TraceMe |> Async.StartAsTask
            
            let closeTasks = 
                clientConnections
                |> Seq.map (fun k -> k.Value |> Seq.map getCloseTask)
                |> Seq.concat
                //|> Seq.append (incommingS2sConnections |> Seq.map getCloseTask)
                |> Seq.append (s2sConnections |> Seq.map getCloseTask)
                |> Seq.append (pendingConnections |> Seq.map getCloseTask)
            let! res = Task.WhenAll(closeTasks)
            ignore res
            return ()
        }
        |> Log.TraceMe
    
    /// Filter all currently registered and open connections and return the result
    member __.FilterConnections filter = filterConnections filter

    interface IConnectionManager with
        
        [<CLIEventAttribute>]
        member x.Errors = x.Errors
        
        [<CLIEventAttribute>]
        member x.ClientNegotiated = x.ClientNegotiated
        
        [<CLIEventAttribute>]
        member x.ClientDisconnected = x.ClientDisconnected
        
        member x.RegisterIncommingConnection (xmppClient) = x.RegisterIncommingConnection(xmppClient)
        member __.RegisterOutgoingConnection _ =
            raise <| System.NotImplementedException("RegisterOutgoingConnection is not implemented.")
        member x.GetConnections jid = x.GetConnections jid
        member x.FilterConnections filter = x.FilterConnections filter
        member x.Shutdown force = x.Shutdown force
