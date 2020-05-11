﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tellurian.Trains.Models.Planning;
using Tellurian.Trains.Repositories.Interfaces;
using Excel = Microsoft.Office.Interop.Excel;

namespace Tellurian.Trains.Repositories.Xpln
{
    public static class Extensions
    {
        public static string Value(this Array row, int col)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            return row.GetValue(1, col)?.ToString();
        }
    }

    public sealed class XplnRepository : ILayoutReadStore, ITimetableReadStore, IScheduleReadStore, IDisposable
    {
        private const string XplnFileSuffix = ".ods";
        private const int TrainIdColumn = 8;
        public readonly DirectoryInfo DocumentsDirectory;
        private Excel.Application _Excel;

        private Excel.Application Excel => _Excel ??= new Excel.Application { Visible = false };

        public XplnRepository(DirectoryInfo documentsDirectory)
        {
            DocumentsDirectory = documentsDirectory ?? throw new ArgumentNullException(nameof(documentsDirectory));
            if (!DocumentsDirectory.Exists) throw new DirectoryNotFoundException(DocumentsDirectory.FullName);
        }

        private string GetFullFilename(string name)
        {
            return Path.Combine(DocumentsDirectory.FullName, string.IsNullOrEmpty(Path.GetExtension(name)) ? name + XplnFileSuffix : name);
        }

        #region Layouts

        public (Maybe<Layout> item, IEnumerable<Message> messages) GetLayout(string name)
        {
            var messages = new List<Message>();
            var result = new Layout(name);
            var app = Excel;
            Excel.Workbook book = null;
            try
            {
                var fileName = GetFullFilename(name);
                book = app.Workbooks.Open(fileName);
                messages.AddRange(GetStations(result, book));
                if (messages.CanContinue()) messages.AddRange(GetStretches(result, book));
            }
            finally
            {
                book?.Close(false, GetFullFilename(name));
            }
            if (messages.HasStoppingErrors()) return (new Maybe<Layout>("Has stopping errors. Import aborted."), messages);
            return (new Maybe<Layout>(result), messages);
        }

        private static IEnumerable<Message> GetStations(Layout layout, Excel.Workbook book)
        {
            var messages = new List<Message>();
            Excel.Worksheet sheet = book.Worksheets["StationTrack"] as Excel.Worksheet;
            var r = 1;
            Station current = null;
            var loop = true;
            while (loop)
            {
                var row = (Array)(sheet.get_Range(Cell("A", r), Cell("G", r)).Cells.Value);
                var col1 = row.Value(1)?.ToString();
                if (col1 == null) break;

                if (col1 != "Name")
                {
                    var rowType = row.Value(6).ToUpperInvariant();
                    switch (rowType)
                    {
                        case "STATION":
                            if (current != null) layout.Add(current);
                            current = new Station(row.Value(5), row.Value(1));
                            break;
                        case "TRACK":

                            current.Add(new StationTrack(row.Value(3)));
                            break;
                        default:
                            loop = false;
                            break;
                    }
                }
                r++;
            }
            if (current != null) layout.Add(current);
            return messages;
        }

        private static IEnumerable<Message> GetStretches(Layout layout, Excel.Workbook book)
        {
            var messages = new List<Message>();
            Excel.Worksheet sheet = book.Worksheets["Routes"] as Excel.Worksheet;
            for (var r = 2; ; r++)
            {
                var row = (Array)sheet.get_Range(Cell("A", r), Cell("I", r)).Cells.Value;
                if (row.GetValue(1, 1) == null)
                {
                    break;
                }
                else
                {
                    var timetableStretchNumber = row.Value(1);
                    var tracksCount = int.Parse(row.Value(8), CultureInfo.InvariantCulture);
                    var fromName = row.Value(3);
                    var toName = row.Value(5);
                    var distance = double.Parse(row.Value(9).Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture);

                    var fromStation = layout.Station(fromName);
                    if (fromStation.IsNone)
                    {
                        messages.Add(Message.Error(fromStation.Message));
                        continue;
                    }

                    var toStation = layout.Station(toName);
                    if (toStation.IsNone)
                    {
                        messages.Add(Message.Error(toStation.Message));
                    }
                    else
                    {
                        var stretch = new TrackStretch(fromStation.Value, toStation.Value, distance, tracksCount);
                        var timetableStretch = layout.TimetableStretches.SingleOrDefault(ts => ts.Number.Equals(timetableStretchNumber, StringComparison.OrdinalIgnoreCase));
                        if (timetableStretch is null)
                        {
                            timetableStretch = new TimetableStretch(timetableStretchNumber);
                            layout.Add(timetableStretch);
                        }
                        var addedStretch = layout.Add(fromStation.Value, toStation.Value, distance, tracksCount);
                        timetableStretch.AddLast(addedStretch);
                    }
                }
            }
            return messages;
        }

        #endregion Layouts

        #region Timetable

        public (Maybe<Timetable> item, IEnumerable<Message> messages) GetTimetable(string name)
        {
            var (layout, layoutMessages) = GetLayout(name);
            if (layoutMessages.HasStoppingErrors())
                return (new Maybe<Timetable>(string.Format(CultureInfo.CurrentCulture, Resources.Strings.CannotReadTimetableDueToErrorsInLayout)), layoutMessages);

            var result = new Timetable(name, layout.Value);
            var app = Excel;
            var messages = new List<Message>(layoutMessages);
            Excel.Workbook book = null;
            try
            {
                book = app.Workbooks.Open(GetFullFilename(name));
                messages.AddRange(GetTrains(result, book));
            }
            catch (Exception ex)
            {
                messages.Add(Message.System(ex.Message));
            }
            finally
            {
                book?.Close(false, GetFullFilename(name));
            }
            if (messages.HasStoppingErrors()) return (new Maybe<Timetable>("Has stopping errors. Import is aborted."), messages);
            return (new Maybe<Timetable>(result), messages);
        }

        private static IEnumerable<Message> GetTrains(Timetable timetable, Excel.Workbook book)
        {
            var messages = new List<Message>();
            Excel.Worksheet sheet = book.Worksheets["Trains"] as Excel.Worksheet;
            var r = 2;
            Train currentTrain = null;
            while (true)
            {
                var row = (Array)sheet.get_Range(Cell("A", r), Cell("K", r)).Cells.Value;
                if (row.GetValue(1, 1) == null)
                {
                    break;
                }
                else
                {
                    var type = row.Value(9).ToUpperInvariant();
                    switch (type)
                    {
                        case "TRAINDEF":
                            if (currentTrain != null) timetable.AddTrain(currentTrain);
                            var trainId = row.Value(TrainIdColumn);
                            currentTrain = new Train(trainId.ParseTrainNumber(), trainId) { Category = trainId.ParsesTrainCategory() };
                            break;

                        case "TIMETABLE":
                            try
                            {
                                var station = timetable.Layout.Station(row.Value(3));
                                var arrivalTime = row.Value(5).AsTime();
                                var departureTime = row.Value(6).AsTime();
                                var note = row.Value(11);
                                if (station.IsNone)
                                {
                                    messages.Add(Message.Error(string.Format(CultureInfo.CurrentCulture, Resources.Strings.ThereIsNoStationWithSignatureOrName, station.Value)));
                                }
                                else
                                {
                                    var trackNumber = row.Value(4);
                                    var track = station.Value.Track(trackNumber);
                                    if (track.IsNone)
                                    {
                                        messages.Add(Message.Error(string.Format(CultureInfo.CurrentCulture, Resources.Strings.TrainAtStationAtTimeRefersToANonexistingTrack, currentTrain, station.Value, arrivalTime, departureTime, trackNumber)));
                                    }

                                    if (messages.CanContinue())
                                    {
                                        var call = new StationCall(track.Value, arrivalTime, departureTime);
                                        if (!string.IsNullOrWhiteSpace(note)) call.Notes.Add(new Note(note));
                                        currentTrain.Add(call);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                messages.Add(Message.System(ex.Message));
                            }
                            break;

                        default:
                            break;
                    }
                }
                r++;
            }
            if (currentTrain != null) timetable.AddTrain(currentTrain);
            return messages;
        }

        #endregion Timetable

        #region Schedule

        public (Maybe<Schedule> item, IEnumerable<Message> messages) GetSchedule(string name)
        {
            var (timetable, timetableMessages) = GetTimetable(name);
            if (timetableMessages.HasStoppingErrors())
                return (new Maybe<Schedule>(string.Format(CultureInfo.CurrentCulture, Resources.Strings.CannotReadScheduleDueToErrorsInTimetable)), timetableMessages);
            var messages = new List<Message>(timetableMessages);
            var result = new Schedule(name, timetable.Value);
            var app = new Excel.Application { Visible = false };
            Excel.Workbook book = null;
            try
            {
                book = app.Workbooks.Open(GetFullFilename(name));
                messages.AddRange(GetSchedules(result, book));
            }
            finally
            {
                book?.Close(false, GetFullFilename(name));
                app.Quit();
            }
            if (messages.HasStoppingErrors()) return (new Maybe<Schedule>("Has stopping errors. Import is aborted."), messages);
            return (new Maybe<Schedule>(result), messages);
        }

        private static IEnumerable<Message> GetSchedules(Schedule schedule, Excel.Workbook book)
        {
            var messages = new List<Message>();
            Excel.Worksheet sheet = book.Worksheets["Trains"] as Excel.Worksheet;
            var r = 2;
            Train currentTrain = null;
            string trainId = null;
            VehicleSchedule currentLoco = null;
            Dictionary<string, VehicleSchedule> locoSchedules = new Dictionary<string, VehicleSchedule>();
            Dictionary<string, DriverDuty> driverDuties = new Dictionary<string, DriverDuty>();
            while (true)
            {
                var row = (Array)sheet.get_Range(Cell("A", r), Cell("K", r)).Cells.Value;
                if (row.GetValue(1, 1) == null)
                {
                    break;
                }
                else
                {
                    var type = row.Value(9).ToUpperInvariant();
                    switch (type)
                    {
                        case "TRAINDEF":
                            trainId = row.Value(8);
                            var train = schedule.Timetable.Train(trainId);
                            if (train.IsNone)
                            {
                                messages.Add(Message.Error($"Train {trainId} cannot be found."));
                                break;
                            }
                            currentTrain = train.Value;
                            break;
                        case "LOCOMOTIVE":
                            var locoId = row.Value(8);
                            if (locoId != null)
                            {
                                if (!locoSchedules.ContainsKey(locoId)) locoSchedules.Add(locoId, VehicleSchedule.Loco(locoId));
                                currentLoco = locoSchedules[locoId];
                                if (currentTrain != null)
                                {
                                    var locoMessages = new List<Message>();
                                    var fromStationSignature = row.Value(3);
                                    var toStationSignature = row.Value(4);
                                    var fromTime = row.Value(5).AsTime();
                                    var toTime = row.Value(6).AsTime();

                                    //if (lt2 < lt1) lt2 = lt2.AddDays(1); // TODO: Handle over midnight times, if necessary
                                    var (fromCall, fromIndex) = currentTrain.FindBetweenArrivlAndBeparture(fromStationSignature, fromTime);
                                    var (toCall, toIndex) = currentTrain.FindBetweenArrivlAndBeparture(toStationSignature, toTime);

                                    if (fromCall.IsNone) locoMessages.Add(Message.Error(string.Format(CultureInfo.CurrentCulture, Resources.Strings.LocoAtStationWithDepartureDoNotRefersToAnExistingTimeInTrain, locoId, fromStationSignature, fromTime, currentTrain)));
                                    if (toCall.IsNone) locoMessages.Add(Message.Error(string.Format(CultureInfo.CurrentCulture, Resources.Strings.LocoAtStationWithArrivalDoNotRefersToAnExistingTimeInTrain, locoId, fromStationSignature, toTime, currentTrain)));
                                    if (fromIndex >= toIndex) locoMessages.Add(Message.Error(string.Format(CultureInfo.CurrentCulture, Resources.Strings.LocoInTrainHasWrongTimingEndStartionIsBeforeStartStation, locoId, currentTrain, fromTime, toTime)));
                                    messages.AddRange(locoMessages);
                                    if (locoMessages.CanContinue())
                                    {
                                        TrainPart trainPart = new TrainPart(fromCall.Value, toCall.Value);
                                        currentLoco.Add(trainPart);
                                    }
                                }
                            }
                            break;
                        case "TRAINSET":
                            var trainsetId = row.Value(8);
                            break;
                        case "JOB":
                            var jobId = row.Value(8);
                            if (jobId != null)
                            {
                                var jobMessages = new List<Message>();
                                if (currentTrain is null)
                                {
                                    messages.Add(Message.Error($"There is not a current train for job {jobId}."));
                                    break;
                                }
                                if (currentLoco is null)
                                {
                                    messages.Add(Message.Error($"There is not a current loco for job {jobId}."));
                                    break;
                                }
                                if (!driverDuties.ContainsKey(jobId)) driverDuties.Add(jobId, new DriverDuty(jobId));
                                var currentLocoSchedule = locoSchedules.Values.SingleOrDefault(l => l.Identity == currentLoco.Identity);
                                if (currentLocoSchedule is null)
                                {
                                    messages.Add(Message.Error($"Job {jobId} referse no a nonexisting loco schedule {currentLoco}."));
                                    break;
                                }
                                var dt1 = row.Value(5).AsTime();
                                var dt2 = row.Value(6).AsTime();
                                if (dt2 < dt1) dt2 = dt2.AddDays(1);
                                var part = currentLocoSchedule.Parts.Select((value, index) => (value, index)).SingleOrDefault(p => p.value.Train.Number == currentTrain.Number && (p.value.From.Arrival == dt1.Value || p.value.From.Departure == dt1.Value || dt1.Value < currentLocoSchedule.Parts.First().From.Arrival) && (p.value.To.Arrival == dt2.Value || p.value.To.Departure == dt2.Value || dt2.Value > currentLocoSchedule.Parts.Last().To.Departure));
                                if (part.value == null) jobMessages.Add(Message.Error($"Error in train {currentTrain} for job {jobId}."));
                                if (jobMessages.CanContinue())
                                {
                                    messages.AddRange(jobMessages);
                                    var added = driverDuties[jobId].Add(new TrainPart(part.value.From, part.value.To));
                                    if (added.IsNone) messages.Add(Message.Error(added.Message));
                                }
                            }
                            break;
                        case "GROUP":
                            if (currentTrain != null) currentTrain.Category = row.Value(8);
                            break;
                        default:
                            break;
                    }
                }
                r++;
            }
            foreach (var loco in locoSchedules.Values) schedule.AddLocoSchedule(loco);
            foreach (var duty in driverDuties.Values) schedule.AddDriverDuty(duty);
            return messages;
        }

        #endregion Schedule

        private static string Cell(string col, int row)
        {
            return col + row.ToString(CultureInfo.InvariantCulture);
        }

        public IEnumerable<string> Save(Layout layout)
        {
            throw new NotSupportedException(nameof(Save));
        }

        public IEnumerable<string> Save(Timetable timetable)
        {
            throw new NotSupportedException(nameof(Save));
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Excel?.Quit();
                }
                _Excel = null;

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~XplnRepository()
        // {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}