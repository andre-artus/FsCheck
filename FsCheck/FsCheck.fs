#light

open System
open System.Collections.Generic
open System.Reflection
open Random
open Microsoft.FSharp.Control
open Microsoft.FSharp.Reflection
 
 
type internal IGen = 
    abstract AsGenObject : Gen<obj>
    
///Generator of a random value, based on a size parameter and a randomly generated int.
and Gen<'a> = 
    Gen of (int -> StdGen -> 'a) 
        ///map the given function to the value in the generator, yielding a new generator of the result type.  
        member x.Map<'a,'b> (f: 'a -> 'b) : Gen<'b> = match x with (Gen g) -> Gen (fun n r -> f <| g n r)
    interface IGen with
        member x.AsGenObject = x.Map box
        //match x with (Gen g) -> Gen (fun n r -> g n r |> box)

///Obtain the current size. sized g calls g, passing it the current size as a parameter.
let sized fgen = Gen (fun n r -> let (Gen m) = fgen n in m n r)

///Override the current size of the test. resize n g invokes generator g with size parameter n.
let resize n (Gen m) = Gen (fun _ r -> m n r)

///Default generator that generates a random number generator. Useful for starting off the process
///of generating a random value.
let rand = Gen (fun n r -> r)

//generates a value out of the generator with maximum size n
let private generate n rnd (Gen m) = 
    let size,rnd' = range (0,n) rnd
    m size rnd'

///The workflow type for generators.
type GenBuilder () =
    member b.Return(a) : Gen<_> = 
        Gen (fun n r -> a)
    member b.Bind((Gen m) : Gen<_>, k : _ -> Gen<_>) : Gen<_> = 
        Gen (fun n r0 -> let r1,r2 = split r0
                         let (Gen m') = k (m n r1) 
                         m' n r2)                                      
    //member b.Let(p, rest) : Gen<_> = rest p
    //not so sure about this one...should delay executing until just before it is executed,
    //for side-effects. Examples are usually like = fun () -> runGen (f ())
    member b.Delay(f : unit -> Gen<_>) : Gen<_> = 
        Gen (fun n r -> match f() with (Gen g) -> g n r )
    member b.TryFinally(Gen m,handler ) = 
        Gen (fun n r -> try m n r finally handler)
    member b.TryWith(Gen m, handler) = 
        Gen (fun n r -> try m n r with e -> handler e)

///The workflow function for generators, e.g. gen { ... }
let gen = GenBuilder()

///Generates an integer between l and h, inclusive.
///Note to QuickCheck users: this function is more general in QuickCheck, generating a Random a.
let choose (l, h) = rand.Map (range (l,h) >> fst) 

///Build a generator that randomly generates one of the values in the given list.
let elements xs = (choose (0, (List.length xs)-1) ).Map(List.nth xs)

///Build a generator that generates a value from one of the generators in the given list, with
///equal probability.
let oneof gens = gen.Bind(elements gens, fun x -> x)

///Build a generator that generates a value from one of the generators in the given list, with
///given probabilities.
let frequency xs = 
    let tot = List.sum_by (fun x -> x) (List.map fst xs)
    let rec pick n ys = match ys with
                        | (k,x)::xs -> if n<=k then x else pick (n-k) xs
                        | _ -> raise (ArgumentException("Bug in frequency function"))
    in gen.Bind(choose (1,tot), fun n -> pick n xs)  

///Lift the given function over values to a function over generators of those values.
let liftGen f = fun a -> gen {  let! a' = a
                                return f a' }

///Lift the given function over values to a function over generators of those values.
let liftGen2 f = fun a b -> gen {   let! a' = a
                                    let! b' = b
                                    return f a' b' }
                                    
///Build a generator that generates a 2-tuple of the values generated by the given generator.
let two g = liftGen2 (fun a b -> (a,b)) g g

///Lift the given function over values to a function over generators of those values.
let liftGen3 f = fun a b c -> gen { let! a' = a
                                    let! b' = b
                                    let! c' = c
                                    return f a' b' c' }

///Build a generator that generates a 3-tuple of the values generated by the given generator.
let three g = liftGen3 (fun a b c -> (a,b,c)) g g g

///Lift the given function over values to a function over generators of those values.
let liftGen4 f = fun a b c d -> gen {   let! a' = a
                                        let! b' = b
                                        let! c' = c
                                        let! d' = d
                                        return f a' b' c' d' }

///Build a generator that generates a 4-tuple of the values generated by the given generator.
let four g = liftGen4 (fun a b c d -> (a,b,c,d)) g g g g

let private fraction (a:int) (b:int) (c:int) = 
    double a + ( double b / abs (double c)) + 1.0 

///Sequence the given list of generators into a generator of a list.
///Note to QuickCheck users: this is monadic sequence, which cannot be expressed generally in F#.
let rec sequence l = match l with
                            | [] -> gen { return [] }
                            | c::cs -> gen {let! x = c
                                            let! xs = sequence cs
                                            return  x::xs } 

///Generates a list of given length, containing values generated by the given generator.
///vector g n generates a list of n t's, if t is the type that g generates.
let vector arbitrary n = sequence [ for i in 1..n -> arbitrary ]

let private promote f = Gen (fun n r -> fun a -> let (Gen m) = f a in m n r)

///Basic co-arbitrary generator, which is dependent on an int.
let variant = fun v (Gen m) ->
    let rec rands r0 = seq { let r1,r2 = split r0 in yield! Seq.cons r1 (rands r2) } 
    Gen (fun n r -> m n (Seq.nth (v+1) (rands r)))

///A collection of default generators.
type Gen =
    ///Generates (), of hte unit type.
    static member Unit = gen { return () }
    ///Generates arbitrary bools
    static member Bool = elements [true; false]
    ///Generates arbitrary ints, between -n and n where n is the test size.
    static member Int = sized <| fun n -> choose (-n,n)
    ///Generates arbitrary floats, NaN included fairly frequently.
    static member Float = liftGen3 fraction Gen.Int Gen.Int Gen.Int
    ///Generates arbitrary chars, between ASCII codes Char.MinValue and 127.
    static member Char = (choose (int Char.MinValue, 127)).Map char 
    ///Generates arbitrary strings, which are lists of chars generated by Char.
    static member String = (Gen.List(Gen.Char)).Map (fun chars -> new String(List.to_array chars))
    ///Generates arbitrary objects, which are generated by Unit, Bool, Int, Float or Char and then cast.
    static member Object = 
        oneof [ (Gen.Unit :> IGen).AsGenObject;
                (Gen.Bool :> IGen).AsGenObject;
                (Gen.Int :> IGen).AsGenObject; 
                (Gen.Float :> IGen).AsGenObject;
                (Gen.Char :> IGen).AsGenObject ]
    ///Genereate a 2-tuple consisting of values generated out of the given generators.
    static member Tuple(a:Gen<'a>,b:Gen<'b>) = liftGen2 (fun x y -> (x,y)) a b
    ///Genereate a 2-tuple consisting of values generated out of the given generators.
    static member Tuple(a:Gen<'a>,b:Gen<'b>,c:Gen<'c>) = liftGen3 (fun x y z -> (x,y,z)) a b c 
    ///Genereate a 2-tuple consisting of values generated out of the given generators.
    static member Tuple(a:Gen<'a>,b:Gen<'b>,c:Gen<'c>,d:Gen<'d>) = liftGen4 (fun x y z w -> (x,y,z,w)) a b c d
    ///Generate an option value, based on the value generated by the given generator.
    static member Option(a) = 
        let arbOption size = 
            match size with
                | 0 -> gen { return None }
                | n -> (a |> resize (n-1)).Map(Some) 
        in sized arbOption
    ///Generate a list of values generated by the given generator. The size of the list is between 0 and the test size.
    static member List<'a>(a : Gen<'a>) : Gen<list<'a>> = sized (fun n -> gen.Bind(choose(0,n), vector a))
    ///Generate a function value, based on the given co-arbitrary generator and result generator.
    static member Arrow(coa,genb) = promote (fun a -> coa a genb)  


type Co =
    static member Unit a = variant 0
    static member Bool b = if b then variant 0 else variant 1
    static member Int n = variant (if n >= 0 then 2*n else 2*(-n) + 1)
    static member Char c = Co.Int (int c)
    static member String s = Co.List (Co.Char) s                        
    static member Tuple (coa,cob) (a,b) = coa a >> cob b
    static member Tuple (coa,cob,coc) (a,b,c) = coa a >> cob b >> coc c
    static member Tuple (coa,cob,coc,cod) (a,b,c,d) = 
          coa a >> cob b >> coc c >> cod d      
    static member Float (fl:float) = //convert float 10.345 to 10345 * 10^-3
        let d1 = sprintf "%g" fl
        let spl = d1.Split([|'.'|])
        let m = if (spl.Length > 1) then spl.[1].Length else 0
        let decodeFloat = (fl * float m |> int, m )
        Co.Tuple(Co.Int, Co.Int) <| decodeFloat
    static member Option coa a =
          match a with 
            | None -> variant 0
            | Some y -> variant 1 >> coa y                                
    static member List coa l = match l with
                                | [] -> variant 0
                                | x::xs -> coa x << variant 1 << Co.List coa xs
    static member Arrow (gena,cob) f (gn:Gen<_>) = 
        gen { let! x = gena
              return! cob (f x) gn }


type Result = { ok : option<Lazy<bool>>
                stamp : list<string>
                arguments : list<obj> 
                exc: option<Exception> }


let nothing = { ok = None; stamp = []; arguments = []; exc = None }

type Property = Prop of Gen<Result>

let result res = gen { return res } |> Prop
                       
let evaluate (Prop gen) = gen

//TODO: check the correctness of try-catch stuff
let forAll gn body = 
    let argument a res = { res with arguments = (box a) :: res.arguments } in
    Prop <|  gen { let! a = gn
                   let! res = 
                       try 
                            (evaluate (body a))
                       with
                            e -> gen { return { nothing with ok = Some (lazy false); exc = Some e }}
                   return (argument a res) }

let emptyProperty = result nothing

let implies b a = if b then a else emptyProperty
let (==>) b a = implies b a

let label str a = 
    let add res = { res with stamp = str :: res.stamp } in
    Prop ((evaluate a).Map add)

let classify b name a = if b then label name a else a

let trivial b = classify b "trivial"

let collect v = label <| any_to_string v

let prop b = gen { return {nothing with ok = Some b}} |> Prop
let propl b = gen { return {nothing with ok = Some (lazy b)}} |> Prop

type TestData = { NumberOfTests: int; Stamps: seq<int * list<string>>}
type TestResult = 
    | True of TestData
    | False of TestData * list<obj> * option<Exception> //the arguments that produced the failed test
    | Exhausted of TestData

type TestStep = 
    | Generated of list<obj>    //test number and generated arguments (test not yet executed)
    | Passed of list<string>    //passed, test number and stamps for this test
    | Falsified of list<obj> * option<Exception>   //falsified the property with given arguments, potentially exception was thrown
    | Failed                    //generated arguments did not pass precondition


type IRunner =
    abstract member OnArguments: int * list<obj> * (int -> list<obj> -> string) -> unit
    abstract member OnFinished: string * TestResult -> unit

type Config = 
    { maxTest : int
      maxFail : int
      name    : string
      size    : float -> float  //determines size passed to the generator as funtion of the previous size. Rounded up.
                            //float is used to allow for smaller increases than 1.
                            //note: in QuickCheck, this is a function of the test number!
      every   : int -> list<obj> -> string  //determines what to print if new arguments args are generated in test n
      runner  : IRunner } //the test runner  


let (|Lazy|) (inp:Lazy<'a>) = inp.Force()             

let rec test initSize resize rnd0 gen =
    seq { let rnd1,rnd2 = split rnd0
          let newSize = resize initSize
          let result = generate (newSize |> round |> int) rnd2 gen
          yield Generated result.arguments
          match result.ok with
            | None -> 
                yield Failed  
                yield! test newSize resize rnd1 gen
            | Some (Lazy true) -> 
                yield Passed result.stamp
                yield! test newSize resize rnd1 gen
            | Some (Lazy false) -> 
                yield Falsified (result.arguments,result.exc)
                yield! test newSize resize rnd1 gen
    }

let testsDone config outcome ntest stamps =    
    let entry (n,xs) = (100 * n / ntest),xs
    let table = stamps 
                |> Seq.filter (fun l -> l <> []) 
                |> Seq.sort_by (fun x -> x) 
                |> Seq.group_by (fun x -> x) 
                |> Seq.map (fun (l, ls) -> (Seq.length ls, l))
                |> Seq.sort_by (fun (l, ls) -> l)
                |> Seq.map entry
                //|> Seq.to_list
                //|> display
    let testResult =
        match outcome with
            | Passed _ -> True { NumberOfTests = ntest; Stamps = table }
            | Falsified (args,exc) -> False ({ NumberOfTests = ntest; Stamps = table }, args, exc)
            | Failed _ -> Exhausted { NumberOfTests = ntest; Stamps = table }
            | _ -> failwith "Test ended prematurely"
    config.runner.OnFinished(config.name,testResult)
    //Console.Write(message outcome + " " + any_to_string ntest + " tests" + table:string)

let runner config property = 
    let testNb = ref 0
    let failedNb = ref 0
    let lastStep = ref Failed
    test 0.0 (config.size) (newSeed()) (evaluate property) |>
    Seq.take_while (fun step ->
        lastStep := step
        match step with
            | Generated args -> config.runner.OnArguments(!testNb, args, config.every); true//Console.Write(config.every !testNb args); true
            | Passed _ -> testNb := !testNb + 1; !testNb <> config.maxTest //stop if we have enough tests
            | Falsified _ -> testNb := !testNb + 1; false //falsified, always stop
            | Failed -> failedNb := !failedNb + 1; !failedNb <> config.maxFail) |> //failed, stop if we have too much failed tests
    Seq.fold (fun acc elem ->
        match elem with
            | Passed stamp -> (stamp :: acc)
            | _ -> acc
    ) [] |>   
    testsDone config !lastStep !testNb

let consoleRunner =
    let display l = match l with
                        | []  -> ".\n"
                        | [x] -> " (" + x + ").\n"
                        | xs  -> ".\n" + List.fold_left (fun acc x -> x + ".\n"+ acc) "" xs
    let rec intersperse sep l = match l with
                                | [] -> []
                                | [x] -> [x]
                                | x::xs -> x :: sep :: intersperse sep xs  
    let entry (p,xs) = any_to_string p + "% " + (intersperse ", " xs |> Seq.to_array |> String.Concat)
    let stamps_to_string s = s |> Seq.map entry |> Seq.to_list |> display
    { new IRunner with
        member x.OnArguments (ntest,args, every) =
            printf "%s" (every ntest args)
        member x.OnFinished(name,testResult) = 
            let name = (name+"-")
            match testResult with
                | True data -> printf "%sOk, passed %i tests%s" name data.NumberOfTests (data.Stamps |> stamps_to_string )
                | False (data, args, None) -> printf "%sFalsifiable, after %i tests: %A\n" name data.NumberOfTests args 
                | False (data, args, Some exc) -> printf "%sFalsifiable, after %i tests: %A\n with exception:\n%O" name data.NumberOfTests args exc
                | Exhausted data -> printf "%sArguments exhausted after %i tests%s" name data.NumberOfTests (data.Stamps |> stamps_to_string )
    }
       
let check config property = runner config property

let quick = { maxTest = 100
              maxFail = 1000
              name    = ""
              size    = fun prevSize -> prevSize + 0.5
              every   = fun ntest args -> "" 
              runner  = consoleRunner } 
         
let verbose = 
    { quick with every = fun n args -> any_to_string n + ":\n" + (args |> List.fold_left (fun b a -> any_to_string a + "\n" + b) "")  }

let quickCheck p = p |> check quick
let verboseCheck p = p |> check verbose
let qcheck gen p = forAll gen p |> quickCheck
let vcheck gen p = forAll gen p |> verboseCheck

//parametrized active pattern that recognizes generic types with generic type definitions equal to the first paramater, 
//and that returns the generic type parameters of the generic type.
let (|GenericTypeDef|_|) (p:Type) (t:Type) = 
    try
        let generic = t.GetGenericTypeDefinition() 
        if p.Equals(generic) then Some(t.GetGenericArguments()) else None
    with _ -> None

let findGenerators = 
    let addMethods l (t:Type) =
        t.GetMethods((BindingFlags.Static ||| BindingFlags.Public)) |>
        Seq.fold (fun l m ->
            //let returnType = m.ReturnType
            let gen = typedefof<Gen<_>>
            match m.ReturnType with
                | GenericTypeDef gen args -> 
                    ((if args.[0].IsGenericType then args.[0].GetGenericTypeDefinition() else args.[0]), m) :: l  
                | _ -> l
            ) l
    addMethods []

let generators = new Dictionary<_,_>()

let registerGenerators t = findGenerators t |> Seq.iter generators.Add //(fun (t,mi) -> generators.Add(t, mi))

registerGenerators (typeof<Gen>)

let rec getGenerator (genericMap:IDictionary<_,_>) (t:Type)  =
    if t.IsGenericParameter then
        if genericMap.ContainsKey(t) then 
            genericMap.[t]
        else
            let newGenerator =  
                generators.Keys 
                |> Seq.filter (fun t -> not t.IsGenericType) 
                |> Seq.map (getGenerator genericMap)
                |> Seq.to_list
                |> elements
                |> generate 0 (newSeed())
            genericMap.Add(t, newGenerator)
            newGenerator
    else
        let t' = if t.IsGenericType then t.GetGenericTypeDefinition() else t
        let mi = generators.[t']
        let args = t.GetGenericArguments() |> Array.map (getGenerator genericMap)
        let typeargs = args |> Array.map (fun o -> o.GetType().GetGenericArguments().[0])
        let mi' = if mi.ContainsGenericParameters then mi.MakeGenericMethod(typeargs) else mi
        mi'.Invoke(null, args)

// resolve fails if the generic type is only determined by the return type 
//(e.g., Array.zero_create) but that is easily fixed by additionally passing in the return type...
let rec resolve (acc:Dictionary<_,_>) (a:Type, f:Type) =
    if f.IsGenericParameter then
        if not (acc.ContainsKey(f)) then acc.Add(f,a)
    else 
        if a.HasElementType then resolve acc (a.GetElementType(), f.GetElementType())
        Array.zip (a.GetGenericArguments()) (f.GetGenericArguments()) |>
        Array.iter (resolve acc)

//for invoking functions: http://cs.hubfs.net/forums/thread/7820.aspx
let invokeMethod (m:MethodInfo) args =
    let m = if m.ContainsGenericParameters then
                let typeMap = new Dictionary<_,_>()
                Array.zip args (m.GetParameters()) |> 
                Array.iter (fun (a,f) -> resolve typeMap (a.GetType(),f.ParameterType))  
                let actuals = 
                    m.GetGenericArguments() |> 
                    Array.map (fun formal -> typeMap.[formal])
                m.MakeGenericMethod(actuals)
            else 
                m
    m.Invoke(null, args)


let qcheckType config (t:Type) = 
    t.GetMethods((BindingFlags.Static ||| BindingFlags.Public)) |>
    Array.map(fun m -> 
        let genericMap = new Dictionary<_,_>()
        //this needs IGen cause can't cast Gen<anything> to Gen<obj> directly (no variance!)
        let gen = m.GetParameters() 
                    |> Array.map(fun p -> (getGenerator genericMap p.ParameterType  :?> IGen).AsGenObject )
                    |> Array.to_list
                    |> sequence
                    |> (fun gen -> gen.Map List.to_array)
        let property args =
            if m.ReturnType = typeof<bool> then
                invokeMethod m args |> unbox<bool> |> propl
            elif m.ReturnType = typeof<Property> then
                invokeMethod m args |> unbox<Property>
            else
                failwith "Invalid return type: must be either bool or Property"
        check {config with name = t.Name+"."+m.Name} (forAll gen property)) |> ignore