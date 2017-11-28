﻿open System
open Proto.FSharp
open Proto.FSharp.Actor

type Person = 
    {
        Age: int
        Name: string
    }

type SetName = { Name : string }
type SetAge = { Age : int }

type Messages =
    | SetName of SetName
    | SetAge of SetAge
    | SystemMessage of SystemMessage
    | Unknown of obj

let (|IsMessage|_|) (msg: obj) =
    match msg with
    | :? SetAge as m -> Some(SetAge m)
    | :? SetName as m -> Some(SetName m)
    | _ -> None

let test2() =
    let handler s m = 
        printfn "Received: %A" m
        let s' = s + "." + m
        printfn "State: %A" s'
        s'

    let pid =
        Actor.withState handler ""
        |> Actor.initProps
        |> Actor.spawn
    pid <! "proto"
    pid <! "actor"

    let pid3 = Actor.create (printfn "Msg: %A") |> Actor.initProps |> Actor.spawn
    [ 1 .. 100 ] |> List.iter (fun i -> pid3 <! i)

    let mapMsg (msg: obj) =
        match msg with
        | IsMessage m -> m
        | IsSystemMessage m -> SystemMessage m
        | _ -> Unknown msg

    let handler4 mapper (mailbox: Actor.Actor<obj>) (msg: obj) state = 
        let state' : Person = 
            match msg |> mapper with
            | SetName m -> 
                printfn "SetName: %A" m
                { state with Name = m.Name }
            | SetAge m -> 
                printfn "SetAge: %A" m
                mailbox.CurrentContext().Respond("You go girl")
                { state with Age = m.Age }
            | SystemMessage m ->
                printfn "System message: %A" m
                state
            | Unknown m -> 
                printfn "unknown %A" m
                mailbox.CurrentContext().Respond(sprintf "You go girl: %A" m)
                state
        printfn "Current state: %A" state'
        state'

    let pid4 =
        Actor.withState2 (handler4 mapMsg) { Name = "John"; Age = 0}
        |> Actor.initProps 
        |> Actor.spawn

    pid4 <! { Name = "tomas" }
    async {
        let! x = { Age = 35 } >? pid4
        let! y = "this isn't handled" >? pid4
        return (x,y)
    } |> Async.RunSynchronously |> (printfn "Reponse %A")
    ()

[<EntryPoint>]
let main argv =
    test2()
    System.Console.ReadLine() |> ignore
    0