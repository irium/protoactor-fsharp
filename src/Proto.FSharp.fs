module Proto.FSharp

open Proto
open System.Threading.Tasks
open System

let inline startAsPlainTask (work : Async<unit>) = Task.Factory.StartNew(fun () -> work |> Async.RunSynchronously)

module System = 
    let toFunc<'a> f = Func<'a>(f)
    let toFunc2<'a, 'b> f = Func<'a, 'b>(f)

module Actor =
    let spawn (props: Props) = Actor.Spawn(props)

    let spawnPrefix prefix (props: Props) = Actor.SpawnPrefix(props, prefix)

    let spawnNamed name (props: Props) = Actor.SpawnNamed(props, name)

    type IO<'T> =
        | Input

    type Actor<'Message> = 
        abstract Receive : unit -> IO<'Message>
        abstract CurrentContext: unit -> IContext


    type Cont<'In, 'Out> = 
        | Func of ('In -> Cont<'In, 'Out>)
        | Return of 'Out

    /// The builder for actor computation expression.
    type ProtoBuilder() = 
    
        /// Binds the next message.
        member __.Bind(m : IO<'In>, f : 'In -> _) = Func(f)
    
        /// Binds the result of another actor computation expression.
        member this.Bind(x : Cont<'In, 'Out1>, f : 'Out1 -> Cont<'In, 'Out2>) : Cont<'In, 'Out2> = 
            match x with
            | Func fx -> Func(fun m -> this.Bind(fx m, f))
            | Return v -> f v

        member __.ReturnFrom(x) = x
        member __.Return x = Return x
        member __.Zero() = Return()
    
        member this.TryWith(f : unit -> Cont<'In, 'Out>, c : exn -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            try 
                true, f()
            with ex -> false, c ex
            |> function 
            | true, Func fn -> Func(fun m -> this.TryWith((fun () -> fn m), c))
            | _, v -> v
    
        member this.TryFinally(f : unit -> Cont<'In, 'Out>, fnl : unit -> unit) : Cont<'In, 'Out> = 
            try 
                match f() with
                | Func fn -> Func(fun m -> this.TryFinally((fun () -> fn m), fnl))
                | r -> 
                    fnl()
                    r
            with ex -> 
                fnl()
                reraise()
    
        member this.Using(d : #IDisposable, f : _ -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            this.TryFinally((fun () -> f d), 
                            fun () -> 
                                if d <> null then d.Dispose())
    
        member this.While(condition : unit -> bool, f : unit -> Cont<'In, unit>) : Cont<'In, unit> = 
            if condition() then 
                match f() with
                | Func fn -> 
                    Func(fun m -> 
                        fn m |> ignore
                        this.While(condition, f))
                | v -> this.While(condition, f)
            else Return()
    
        member __.For(source : 'Iter seq, f : 'Iter -> Cont<'In, unit>) : Cont<'In, unit> = 
            use e = source.GetEnumerator()
        
            let rec loop() = 
                if e.MoveNext() then 
                    match f e.Current with
                    | Func fn -> 
                        Func(fun m -> 
                            fn m |> ignore
                            loop())
                    | r -> loop()
                else Return()
            loop()
    
        member __.Delay(f : unit -> Cont<_, _>) = f
        member __.Run(f : unit -> Cont<_, _>) = f()
        member __.Run(f : Cont<_, _>) = f
    
        member this.Combine(f : unit -> Cont<'In, _>, g : unit -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f() with
            | Func fx -> Func(fun m -> this.Combine((fun () -> fx m), g))
            | Return _ -> g()
    
        member this.Combine(f : Cont<'In, _>, g : unit -> Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f with
            | Func fx -> Func(fun m -> this.Combine(fx m, g))
            | Return _ -> g()
    
        member this.Combine(f : unit -> Cont<'In, _>, g : Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f() with
            | Func fx -> Func(fun m -> this.Combine((fun () -> fx m), g))
            | Return _ -> g
    
        member this.Combine(f : Cont<'In, _>, g : Cont<'In, 'Out>) : Cont<'In, 'Out> = 
            match f with
            | Func fx -> Func(fun m -> this.Combine(fx m, g))
            | Return _ -> g

    type FSharpActor<'Message, 'ReturnType>(createActor: Actor<'Message> -> Cont<'Message, 'Returned>) as this = 
        let actor = 
            { 
                new Actor<'T1> 
                    with 
                        member __.Receive() = Input
                        member __.CurrentContext() = this.CurrentContext()}
        let mutable state = createActor actor
        let mutable ctx: IContext = null
        member __.CurrentContext() = 
            ctx

        interface IActor with
            member this.ReceiveAsync(context: IContext) =
                async {
                    match state with
                    | Func f ->
                        ctx <- context
                        match context.Message with
                        | :? 'T1 as msg -> 
                            state <- f msg
                        | _ -> ()
                    | Return x -> x
                } |> startAsPlainTask

    let initProps (createActor: Actor<'Message> -> Cont<'Message, 'Returned>) =
        let producer () = new FSharpActor<'Message, 'Returned>(createActor) :> IActor
        let producerFunc = System.Func<_>(producer)
        Actor.FromProducer(producerFunc)

    let proto = ProtoBuilder()

    let withState2 (handler: Actor<'Message> -> 'Message -> 'State -> 'State) (initialState: 'State) (mailbox : Actor<'Message>) =
        let rec loop state = 
            proto {
                let! msg = mailbox.Receive()
                let state' = state |> handler mailbox msg
                return! loop state'
            }
        loop initialState

    let withState (handler: 'Message -> 'State -> 'State) (initialState: 'State) =
        withState2 (fun _ m s -> handler m s) initialState

    let create (handler: 'Message -> unit) =
        withState2 (fun _ m _ -> handler m) ()

    let create2 (handler: Actor<'Message> -> 'Message -> unit) =
        withState2 (fun mb m _ -> handler mb m) ()


[<AutoOpen>]
module Props = 
    open System

    let newProps() = Props()

    let withProducer producer (props: Props) = 
        props.WithProducer(producer)

    let withDispatcher dispatcher (props: Props) = 
        props.WithDispatcher(dispatcher)

    let withMailbox mailbox (props: Props) = 
        props.WithMailbox(mailbox)

    let withChildSupervisorStrategy supervisorStrategy (props: Props) =
        props.WithChildSupervisorStrategy(supervisorStrategy)

    let withReceiveMiddleware (middleware: Receive -> Receive) (props: Props) =
        props.WithReceiveMiddleware([|toFunc2(middleware)|])

    let withReceiveMiddlewares (middlewares: (Receive -> Receive) list) (props: Props) =
        middlewares 
        |> List.map toFunc2
        |> Array.ofList
        |> (fun arr -> props.WithReceiveMiddleware(arr))

    let withSenderMiddleware (middleware: Sender -> Sender) (props: Props) =
        props.WithSenderMiddleware([|toFunc2(middleware)|])

    let withSenderMiddlewares (middlewares: (Sender -> Sender) list) (props: Props) =
        middlewares 
        |> List.map toFunc2
        |> Array.ofList
        |> (fun arr -> props.WithSenderMiddleware(arr))

    let withSpawner spawner (props: Props) = 
        props.WithSpawner(spawner)

[<AutoOpen>]
module Pid = 
    let tell (pid: PID) msg = 
        pid.Tell(msg)
    
    let (<!) (pid: PID) msg = tell pid msg