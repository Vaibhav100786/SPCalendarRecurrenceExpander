﻿module CompilerPropertiesTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Swensen.Unquote
open Holm.SPCalendarRecurrenceExpander

let genId = Arb.Default.PositiveInt().Generator |> Gen.map int
let genStart = Arb.Default.DateTime().Generator |> Gen.suchThat (fun d -> d >= DateTime(1900, 1, 1, 0, 0, 0))

// according to UI between 0h00m and 23h55m for recurrence events
let genDuration = Gen.choose(0, (23 * 60 * 60) + (55 * 60))

let singleAppointment =
    let genEnd start = Gen.suchThat(fun d -> d >= start && d <= DateTime(8900, 12, 31, 23, 59, 0)) genStart

    gen {
        let! id = genId
        let! start = genStart
        let! endDate = genEnd start
        let duration = (endDate - start).TotalSeconds |> int64

        return {
            Id = id
            Start = start
            End = endDate
            Duration = int64 duration
            Recurrence = NoRecurrence } }

let dailyEveryNthDaysAppointment =
    gen { 
        let! a = singleAppointment
        let! duration = genDuration

        // SharePoint computes end date on saving an event through the UI so it can
        // be used in queries without first expanding all events. We don't have this
        // functionality available and thus ensure recurrences end no less than 1x
        // duration apart.
        let genEnd = Gen.suchThat(fun d -> 
            d >= a.Start.AddSeconds(float duration) && d <= DateTime(8900, 12, 31, 23, 59, 0))
        
        // bounds according to UI message
        let! n = Gen.choose(1, 255)

        return { 
            a with
                Duration = int64 duration
                Recurrence = Daily(EveryNthDay n, ImplicitEnd) } }

type SingleAppointmentGenerator = 
    static member Appointment() = 
        { new Arbitrary<Appointment>() with
            override x.Generator = singleAppointment }

type DailyEveryNthDaysAppointment = 
    static member Appointment() = 
        { new Arbitrary<Appointment>() with
            override x.Generator = dailyEveryNthDaysAppointment }
 
let fsCheck testable =
    //Check.One({Config.Verbose with MaxTest = 100; Runner = NUnitRunner()}, testable)
    ()

let sut = Compiler()

[<Fact>]
let ``identity appointment on no recurrence``() =   
    Arb.register<SingleAppointmentGenerator>() |> ignore

    fsCheck <| fun (a: Appointment) ->
        let r = sut.Compile(a, [], [])
        let h = r |> Seq.head 
        Assert.Equal(1, r |> Seq.length)
        Assert.Equal({RecurrenceInstance.Id = a.Id; Start = a.Start; End = a.End}, h)

[<Fact>]
let ``daily instances between start and end date and correctly spaced``() =
    Arb.register<DailyEveryNthDaysAppointment>() |> ignore

    fsCheck <| fun (a: Appointment) ->
        let r = sut.Compile(a, [], [])    
        let n = 
            match a.Recurrence with
            | Daily(p, _) ->
                match p with
                | EveryNthDay n -> n
                | _ -> Int32.MinValue
            | _ -> Int32.MinValue

        // id preservered across instances
        Seq.iter (fun r -> Assert.Equal(a.Id, r.Id)) |> ignore

        // appointments start and end within window
        r |> Seq.head |> fun r -> Assert.True(a.Start >= r.Start)
        r |> Seq.last |> fun r -> Assert.True(r.End < a.End)

        // appointments start n days apart
        r
        |> Seq.pairwise 
        |> Seq.map (fun (a, b) -> (b.Start - a.Start).TotalSeconds |> string |> Int64.Parse)
        |> Seq.iter (fun a -> Assert.Equal(86400L * (int64 n), a))