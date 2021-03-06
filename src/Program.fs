﻿[<FunScript.JS>]
module Program

open System
open System.IO
open System.Diagnostics
open FunScript.Compiler
open FunScript.TypeScript

[<ReflectedDefinition>]
module ClientApplication =

    type FloodFillMode = Stack | Queue | Scanline

    type Action = Set | Clear | FloodFill of FloodFillMode

    type Pixel = Filled | Cleared

    type History = SetPixel of (int*int)*Pixel | Step of int

    type IPixelBox =
        abstract Item: int*int -> Pixel with get, set
        abstract Width: int
        abstract Height: int
        abstract Reset: unit -> unit
        abstract Step: int -> unit
        abstract CopyTo: IPixelBox -> unit

    type Recorder(source: IPixelBox) =

        let sourceCopy () = 
            [0..(source.Width-1)] |> List.collect (fun x ->
                [0..(source.Height-1)] |> List.map (fun y -> x,y)
            )
            |> List.map (fun (x,y) -> ((x,y), source.[x,y]))
            |> Map.ofList   

        let mutable pixels = sourceCopy ()

        let mutable history: History list = []

        let playback (destination: IPixelBox) =
            
            let delay (time: int) (fn: unit -> unit) = Globals.window.setTimeout(unbox<Function> fn,time) |> ignore

            let rec playback history () =
                match history with
                | head::rest ->
                    match head with
                    | SetPixel ((x,y),state) ->
                        destination.[x,y] <- state
                        playback rest |> delay 0
                    | Step time ->
                        playback rest |> delay time
                | [] -> ()

            do history |> List.rev |> playback <| ()

        interface IPixelBox with
            member this.Width = source.Width
            member this.Height = source.Height
            member this.Item
                with get (x,y) = pixels |> Map.find (x,y)
                and set (x,y) state =
                    pixels <- pixels |> Map.add (x,y) state
                    history <- SetPixel ((x,y), state) :: history
            member this.Reset () =
                pixels <- sourceCopy ()
                history <- []
            member this.Step delay =
                history <- Step delay :: history
            member this.CopyTo dest = do playback dest

    type HtmlPixelBox(element: Element) =

        let ``$`` = Globals.Dollar
 
        let width = ``$``.Invoke(element).data("width") |> unbox<int>
        let height = ``$``.Invoke(element).data("height") |> unbox<int>

        let td x y =
            ``$``.Invoke("tr",element).eq(float <| y).find("td").eq(float <| x)

        let readPixel (x,y) = if (td x y).hasClass "filled" then Filled else Cleared

        do
            for y in {1..height} do
                let row = ``$``.Invoke("<tr></tr>")
                for x in {1..width} do
                    let cell = ``$``.Invoke("<td></td>")
                    do row.append(cell) |> ignore
                    do cell.data("x",x-1) |> ignore
                    do cell.data("y",y-1) |> ignore
                (``$``.Invoke element).append row |> ignore   

        member this.Table = element

        member this.CoordinatesFromElement (element: Element) =
            let element = ``$``.Invoke(element)
            element.data "x" |> unbox<int>, element.data "y" |> unbox<int>


        interface IPixelBox with
            member this.Width = width
            member this.Height = height

            member this.Item
                with get (x,y) = readPixel (x,y)
                and set (x,y) state =
                    let td = td x y
                    let currentlyFilled = td.hasClass "filled"
                    match state with
                    | Filled when not currentlyFilled ->
                        td.addClass "filled" |> ignore
                    | Cleared when currentlyFilled ->
                        td.removeClass "filled" |> ignore
                    | _ -> () // State is already correct
            member this.Reset () =
                ``$``.Invoke("td",element).removeClass "filled" |> ignore

            member this.Step _ = ()

            member this.CopyTo destination =
                for x = 0 to (width-1) do
                    for y = 0 to (height-1) do
                        destination.[x,y] <- readPixel (x,y)

    let main (samples: string) = 
        let ``$`` = Globals.Dollar


        let samples = samples.Split('|')

        let select (selector: string) = ``$``.Invoke selector

        ``$``.Invoke(unbox<Function> (fun () ->

            let screen = HtmlPixelBox(select "#main-pixel-box" :?> Element)

            let mode () =
                (select "#mode-select option:selected")._val ()
                |> unbox<string>
                |> function
                    | "set" -> Set
                    | "clear" -> Clear
                    | "flood_stack" -> FloodFill Stack
                    | "flood_queue" -> FloodFill Queue
                    | "flood_scanline" -> FloodFill Scanline
                    |_ -> failwith "Not implemented"

            let floodfill (pixelbox: IPixelBox) mode point =
                let neighbours (x,y) =
                    [(-1,0); (1,0); (0,-1); (0,1)]
                    |> List.map (fun (dx,dy) -> x+dx,y+dy)
                    |> List.filter (fun (x,y) -> x >= 0 && x < pixelbox.Width && y >= 0 && y < pixelbox.Height)

                let neighbourFill useQueue pixels =

                    let mutable pixels: (int*int) list = pixels

                    while pixels.Length > 0 do
                        pixels <-
                            match pixels with
                            | (x,y) :: rest when pixelbox.[x,y] = Cleared ->
                                do pixelbox.[x,y] <- Filled
                                if not useQueue then
                                    (neighbours (x,y)) @ rest
                                else
                                    rest @ (neighbours (x,y))
                            | _::rest -> rest
                            | [] -> []

                let rec scanlineFill (x,y) =
                    let rec fillLine op x =
                        if x >= 0 && x < pixelbox.Width && pixelbox.[x,y] = Cleared then
                            pixelbox.[x,y] <- Filled
                            fillLine op (op x)
                        else
                            x
                    let x1 = (fillLine (fun x -> x - 1) (x-1)) + 1
                    do pixelbox.Step 50
                    let x2 = (fillLine (fun x -> x + 1) x) - 1
                    do pixelbox.Step 50

                    let findSeeds y =
                        [x1..x2]
                        |> List.map (fun x -> x, pixelbox.[x,y]) 
                        |> List.fold (fun (last,folded) (x,pixel) ->
                            if pixel = Cleared && last = Filled then pixel, x :: folded
                            else pixel, folded
                        ) (Filled,[])
                        |> snd
                        |> List.map (fun x -> x,y)
                    let above = if (y-1) >= 0 then findSeeds (y-1) else []
                    let below = if (y+1) < pixelbox.Height then findSeeds (y+1) else []
                    above @ below |> List.iter scanlineFill

                match mode with
                | Stack -> neighbourFill false [point]
                | Queue -> neighbourFill true [point]
                | Scanline -> scanlineFill point
                  
            let pixelActivated (x, y) =
                let pixelbox = screen :> IPixelBox
                match mode () with
                | Set -> do pixelbox.[x,y] <- Filled
                | Clear -> do pixelbox.[x,y] <- Cleared
                | FloodFill mode ->
                    let recorder = Recorder(pixelbox) :> IPixelBox
                    do floodfill recorder mode (x,y)
                    do recorder.CopyTo pixelbox

            do
            
                let mutable mouseDown = false

                let targetToCoordinates (target: EventTarget) = screen.CoordinatesFromElement (target :?> Element)

                let onHandler fn = Func<JQueryEventObject,obj array,obj>(fun e _ -> fn e; null) 

                let clickHandler fn = Func<JQueryEventObject,obj>(fun e -> fn e; null) 

                ``$``.Invoke(screen.Table).on("mousedown","td",onHandler (fun e ->
                    do e.target |> targetToCoordinates |> pixelActivated
                    do mouseDown <- true
                )) |> ignore

                ``$``.Invoke(screen.Table).on("mouseenter","td",onHandler (fun e ->
                    if mouseDown then
                        do e.target |> targetToCoordinates |> pixelActivated
                )) |> ignore

                ``$``.Invoke(screen.Table).on("mouseup","td",onHandler (fun e ->
                    do mouseDown <- false
                )) |> ignore

                (select "#clear-all").click (clickHandler (fun _ ->
                    do (screen :> IPixelBox).Reset ()
                )) |> ignore


                let initSampleBox (data: string) (element: Element)=

                    let screen = screen :> IPixelBox

                    ``$``.Invoke(element).click (clickHandler (fun _ ->
                         {0..(screen.Width-1)} |> Seq.iter (fun x ->
                                {0..(screen.Height-1)} |> Seq.iter (fun y ->
                                    if data.Substring(x*screen.Width + y,1) = "1" then
                                        screen.[x,y] <- Filled 
                                    else screen.[x,y] <- Cleared
                                )
                            )
                    )) |> ignore


                samples |> Array.iteri (fun i sample ->
                    do
                        select (sprintf "#sample-pattern-%d" i) :?> Element
                        |> initSampleBox sample
                )
        ))

do
    // Copy all samples as one gigantic string blob to
    // js as funscript doesn't transate F# data very well.
    let samples = [Samples.rays
                   Samples.egypt
                   Samples.atlas
                   Samples.cat
                   Samples.bubbles
                   Samples.maze] |> String.concat "|"
    let js = Compiler.Compile(<@ ClientApplication.main samples @>, noReturn = true)
    File.WriteAllText("app.js", js)
    Process.Start("index.html") |> ignore