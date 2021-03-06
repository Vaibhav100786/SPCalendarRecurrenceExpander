﻿namespace Holm.SPCalendarRecurrenceExpander

open System
open System.Globalization
open System.Collections.Generic
open System.Text.RegularExpressions

type Dow = DayOfWeek
type kv = Dictionary<string, obj>

module Parsers =
    let recurrence (d: kv) = d.["fRecurrence"] :?> bool
    let eventType (d: kv) = d.["EventType"] :?> int
    let masterSeriesItemId (d: kv) = d.["MasterSeriesItemID"] :?> int
    let recurrenceId (d: kv) = d.["RecurrenceID"] :?> DateTime
    let endDate (d: kv) = d.["EndDate"] :?> DateTime
    let id (d: kv) = d.["ID"] :?> Int32
    let eventDate (d: kv) = d.["EventDate"] :?> DateTime
    let duration (d: kv) = d.["Duration"] |> string |> Int64.Parse
    let allDayEvent (d: kv) = d.["fAllDayEvent"] |> string |> bool.Parse
    let recurrenceData (d: kv) = 
        // connecting a SharePoint calendar to Outlook, Outlook can not only 
        // display SharePoint recurrence appointments, but also create those. 
        // Outlook has its own version of SharePoint's dialogs for different
        // recurrence types. The only difference between SharePoint and Outlook
        // created appointments is that Outlook uses ' whereas SharePoint uses
        // " within RecurrenceData.    
        (d.["RecurrenceData"] |> string).Replace("'","\"")

open Parsers

type KindOfDayQualifier =
    | First
    | Second
    | Third
    | Fourth
    | Last

type KindOfDay =
    | Day
    | Weekday
    | WeekendDay
    | DayOfWeek of Dow

type End =
    | ImplicitEnd (* SP defaults to 999 instances *)
    | RepeatInstances of int
    | ExplicitEnd of DateTime

type DailyPattern =
    | EveryNthDay of int
    | EveryWeekDay

type WeeklyPattern =
    | EveryNthWeekOnDays of int * Set<Dow>

type MonthlyPattern =
    | EveryNthDayOfEveryMthMonth of int * int
    | EveryQualifierOfKindOfDayEveryMthMonth of KindOfDayQualifier * KindOfDay * int

type YearlyPattern =
    | EveryNthDayOfEveryMMonth of int * int
    | EveryQualifierOfKindOfDayMMonth of KindOfDayQualifier * KindOfDay * int

type MasterSeriesItemId = int

type Recurrence =
    | NoRecurrence
    | UnknownRecurrence
    | DeletedRecurrenceInstance of MasterSeriesItemId * DateTime
    | ModifiedRecurrenceInstance of MasterSeriesItemId * DateTime
    | Daily of DailyPattern * End
    | Weekly of WeeklyPattern * End
    | Monthly of MonthlyPattern * End
    | Yearly of YearlyPattern * End

type Appointment =
    { Id: int
      Start: DateTime
      End: DateTime
      Duration: int64
      Recurrence: Recurrence }

type Parser() =
    let groupAsString (m: Match) (g: string) = m.Groups.[g].Value
    let groupAsInt (m: Match) (g: string) = (groupAsString m g) |> int

    let groupAsDateTime (m: Match) (g: string) = 
        let s = groupAsString m g
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

    let (|DailyEveryNthDay|_|) s =
        let re = Regex("<daily dayFrequency=\"(?<dayFrequency>(\d+))\" />")
        let m = re.Match(s)
        if m.Success then Some (groupAsInt m "dayFrequency")
        else None        

    let (|DailyEveryWeekDay|_|) s =
        let re = Regex("<daily weekday=\"TRUE\" />")
        if re.Match(s).Success then Some(0) else None

    let (|WeeklyEveryNthWeekOnDays|_|) s =
        let re = Regex("<repeat><weekly (?<su>(su=\"TRUE\")?).?(?<mo>(mo=\"TRUE\")?).?(?<tu>(tu=\"TRUE\")?).?(?<we>(we=\"TRUE\")?).?(?<th>(th=\"TRUE\")?).?(?<fr>(fr=\"TRUE\")?).?(?<sa>(sa=\"TRUE\")?).?weekFrequency=\"(?<weekFrequency>(\d+))\" /></repeat>")
        let m = re.Match(s)
        
        if m.Success then
            let g (k: string) = m.Groups.[k].Length > 0
            let days = List<DayOfWeek>()
            if g "su" then days.Add(Dow.Sunday)
            if g "mo" then days.Add(Dow.Monday)
            if g "tu" then days.Add(Dow.Tuesday)
            if g "we" then days.Add(Dow.Wednesday)
            if g "th" then days.Add(Dow.Thursday)
            if g "fr" then days.Add(Dow.Friday)
            if g "sa" then days.Add(Dow.Saturday) 
            Some (groupAsInt m "weekFrequency", days |> Set.ofSeq)
        else None

    let (|MonthlyEveryNthDayOfEveryMthMonth|_|) s =
        let re = Regex("<repeat><monthly monthFrequency=\"(?<monthFrequency>(\d+))\" day=\"(?<day>(\d+))\" /></repeat>")
        let m = re.Match(s)
        if m.Success then Some (groupAsInt m "day", groupAsInt m "monthFrequency")
        else None

    let (|KindOfDayQualifier|_|) = function
        | "first" -> Some First
        | "second" -> Some Second
        | "third" -> Some Third
        | "fourth" -> Some Fourth
        | "last" -> Some Last
        | _ -> None

    let (|KindOfDay|_|) = function
        | "day" -> Some Day
        | "weekday" -> Some Weekday
        | "weekend_day" -> Some WeekendDay
        | "su" -> Some(DayOfWeek(Dow.Sunday))
        | "mo" -> Some(DayOfWeek(Dow.Monday))
        | "tu" -> Some(DayOfWeek(Dow.Tuesday))
        | "we" -> Some(DayOfWeek(Dow.Wednesday))
        | "th" -> Some(DayOfWeek(Dow.Thursday))
        | "fr" -> Some(DayOfWeek(Dow.Friday))
        | "sa" -> Some(DayOfWeek(Dow.Saturday))
        | _ -> None

    let (|MonthlyEveryQualifierOfKindOfDayEveryMthMonth|_|) s =
        let re = Regex("<repeat><monthlyByDay (?<kindOfDay>(day|weekday|weekend_day|su|mo|tu|we|th|fr|sa))=\"TRUE\" weekdayOfMonth=\"(?<kindOfDayQualifier>(first|second|third|fourth|last))\" monthFrequency=\"(?<monthFrequency>(\d+))\" /></repeat>")
        let m = re.Match(s)
        if m.Success then 
            let kindOfDayQualifier = 
                match groupAsString m "kindOfDayQualifier" with
                | KindOfDayQualifier q -> q
                | _ -> failwithf "Unable to parse kindOfDayQualifier: %s" (groupAsString m "kindOfDayQualifier")

            let kindOfDay = 
                match groupAsString m "kindOfDay" with
                | KindOfDay d -> d
                | _ -> failwithf "Unable to parse kindOfDay: %s" (groupAsString m "kindOfDay")

            Some (kindOfDayQualifier, kindOfDay, groupAsInt m "monthFrequency")
        else None

    let (|YearlyEveryNthDayOfEveryMMonth|_|) s =
        let re = Regex("<repeat><yearly yearFrequency=\"1\" month=\"(?<month>(\d+))\" day=\"(?<day>(\d+))\" /></repeat>")
        let m = re.Match(s)
        if m.Success then Some(groupAsInt m "day", groupAsInt m "month")
        else None

    let (|YearlyEveryQualifierOfKindOfDayNMonth|_|) s =
        let re = Regex("<repeat><yearlyByDay yearFrequency=\"1\" (?<kindOfDay>(day|weekday|weekend_day|su|mo|tu|we|th|fr|sa))=\"TRUE\" weekdayOfMonth=\"(?<kindOfDayQualifier>(first|second|third|fourth|last))\" month=\"(?<month>(\d+))\" /></repeat>")
        let m = re.Match(s)
        if m.Success then
            let kindOfDayQualifier = 
                match groupAsString m "kindOfDayQualifier" with
                | KindOfDayQualifier q -> q
                | _ -> failwithf "Unable to parse kindOfDayQualifier: %s" (groupAsString m "kindOfDayQualifier")

            let kindOfDay = 
                match groupAsString m "kindOfDay" with
                | KindOfDay d -> d
                | _ -> failwithf "Unable to parse kindOfDay: %s" (groupAsString m "kindOfDay")

            Some (kindOfDayQualifier, kindOfDay, groupAsInt m "month")
        else None

    let (|ImplicitEnd|_|) s =
        let re = Regex("<repeatForever>FALSE</repeatForever>")
        if re.Match(s).Success then Some() else None

    let (|RepeatInstances|_|) s =
        let re = Regex("<repeatInstances>(?<repeatInstances>(\d+))</repeatInstances>")
        let m = re.Match(s)
        if m.Success then Some(groupAsInt m "repeatInstances")
        else None

    let (|ExplicitEnd|_|) s =
        let re = Regex("<windowEnd>(?<windowEnd>(.*?))</windowEnd>")
        let m = re.Match(s)
        if m.Success then Some(groupAsDateTime m "windowEnd")
        else None       

    let parseRecurrence(d: Dictionary<string, obj>) =
        if recurrence d then 
            if eventType d = 3 then
                DeletedRecurrenceInstance(masterSeriesItemId d, recurrenceId d)
            else if eventType d = 4 then
                ModifiedRecurrenceInstance(masterSeriesItemId d, recurrenceId d)
            else
                let endDateTime =
                    match recurrenceData d with
                    | ImplicitEnd _ -> ImplicitEnd
                    | RepeatInstances n -> RepeatInstances n
                    | ExplicitEnd dt -> 
                        // ExplicitEnd contains both a date and a time component expressed
                        // in the same timezone as the start and end dates of the event.
                        // ExplicitEnd is equal to end date and time of the last recurrence
                        // event except when end is at midnight (00:00am) in which case 
                        // ExplicitEnd is 24 hours ahead.
                        if dt.Hour = 0 && dt.Minute = 0 
                        then ExplicitEnd dt
                        else ExplicitEnd (endDate d)
                    | _ -> failwith "Unable to parse end"

                match recurrenceData d with
                | DailyEveryNthDay n -> Daily(EveryNthDay n, endDateTime)
                | DailyEveryWeekDay _ -> Daily(EveryWeekDay, endDateTime)
                | WeeklyEveryNthWeekOnDays (n, days) -> Weekly(EveryNthWeekOnDays(n, days), endDateTime)
                | MonthlyEveryNthDayOfEveryMthMonth (d, m) -> Monthly(MonthlyPattern.EveryNthDayOfEveryMthMonth(d, m), endDateTime)
                | MonthlyEveryQualifierOfKindOfDayEveryMthMonth (q, k, n) -> Monthly(EveryQualifierOfKindOfDayEveryMthMonth(q, k, n), endDateTime)
                | YearlyEveryNthDayOfEveryMMonth (n, m) -> Yearly(EveryNthDayOfEveryMMonth(n, m), endDateTime)
                | YearlyEveryQualifierOfKindOfDayNMonth (q, k, n) -> Yearly(EveryQualifierOfKindOfDayMMonth(q, k, n), endDateTime)
                | _ -> UnknownRecurrence
        else NoRecurrence

    let parse (a: Dictionary<string, obj>) =
        { Id = id a
          Start = eventDate a
          End = endDate a
          Duration = duration a
          Recurrence = parseRecurrence a }

    member __.Parse(appointment: Dictionary<string, obj>) =
        parse appointment