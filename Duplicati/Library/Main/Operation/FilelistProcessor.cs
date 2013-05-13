//  Copyright (C) 2011, Kenneth Skovhede

//  http://www.hexad.dk, opensource@hexad.dk
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using Duplicati.Library.Main.Database;
using System.Collections.Generic;
using System.Linq;

namespace Duplicati.Library.Main
{
    public static class FilelistProcessor
    {
        /// <summary>
        /// Helper method that verifies uploaded volumes and updates their state in the database.
        /// Throws an error if there are issues with the remote storage
        /// </summary>
        /// <param name="backend">The backend instance to use</param>
        /// <param name="options">The options used</param>
        /// <param name="database">The database to compare with</param>
        public static void VerifyRemoteList(BackendManager backend, Options options, LocalDatabase database, CommunicationStatistics stat)
        {
            var tp = RemoteListAnalysis(backend, options, database);
            long extraCount = 0;
            long missingCount = 0;
            
            foreach(var n in tp.ExtraVolumes)
            {
                if (!options.QuietConsole)
                    stat.LogMessage(string.Format("Extra unknown file: {0}", n.File.Name));
                stat.LogWarning(string.Format("Extra unknown file: {0}", n.File.Name), null);
                extraCount++;
            }

            foreach(var n in tp.MissingVolumes)
            {
                if (!options.QuietConsole)
                    stat.LogMessage(string.Format("Missing file: {0}", n.Name));
                stat.LogWarning(string.Format("Missing file: {0}", n.Name), null);
                missingCount++;
            }

            if (extraCount > 0)
                throw new Exception(string.Format("Found {0} remote files that are not recorded in local storage, please run cleanup", extraCount));

            if (missingCount > 0)
            {
                if (!tp.BackupPrefixes.Contains(options.Prefix) && tp.BackupPrefixes.Length > 0)
                    throw new Exception(string.Format("Found {0} files that are missing from the remote storage, and no files with the backup prefix {1}, but found the following backup prefixes: {2}", missingCount, options.Prefix, string.Join(", ", tp.BackupPrefixes)));
                else
                    throw new Exception(string.Format("Found {0} files that are missing from the remote storage, please run cleanup", missingCount));
            }
        }

        public struct RemoteAnalysisResult
        {
            public IEnumerable<Volumes.IParsedVolume> ParsedVolumes;
            public IEnumerable<Volumes.IParsedVolume> ExtraVolumes;
            public IEnumerable<RemoteVolumeEntry> MissingVolumes;
            
            public string[] BackupPrefixes { get { return ParsedVolumes.Select(x => x.Prefix).Distinct().ToArray(); } }
        }

        /// <summary>
        /// Helper method that verifies uploaded volumes and updates their state in the database.
        /// Throws an error if there are issues with the remote storage
        /// </summary>
        /// <param name="backend">The backend instance to use</param>
        /// <param name="options">The options used</param>
        /// <param name="database">The database to compare with</param>
        public static RemoteAnalysisResult RemoteListAnalysis(BackendManager backend, Options options, LocalDatabase database)
        {
            var rawlist = backend.List();
            var lookup = new Dictionary<string, Volumes.IParsedVolume>();

            var remotelist = from n in rawlist let p = Volumes.VolumeBase.ParseFilename(n) where p != null select p;
            foreach (var s in remotelist)
                if (s.Prefix == options.Prefix)
                    lookup[s.File.Name] = s;

            var missing = new List<RemoteVolumeEntry>();
            var locallist = database.GetRemoteVolumes();
            foreach (var i in locallist)
            {
                //Ignore those that are deleted
                if (i.State == RemoteVolumeState.Deleted)
                    continue;
                    
                if (i.State == RemoteVolumeState.Temporary)
                {
                    database.LogMessage("info", string.Format("removing file listed as {0}: {1}", i.State, i.Name), null, null);
                    database.RemoveRemoteVolume(i.Name, null);
                }
                else
                {
                    Volumes.IParsedVolume r;
                    if (!lookup.TryGetValue(i.Name, out r))
                    {
                        if (i.State == RemoteVolumeState.Uploading || i.State == RemoteVolumeState.Deleting || (r != null && r.File.Size != i.Size && r.File.Size >= 0 && i.Size >= 0))
                        {
                            database.LogMessage("info", string.Format("removing file listed as {0}: {1}", i.State, i.Name), null, null);
                            database.RemoveRemoteVolume(i.Name, null);
                        }
                        else
                            missing.Add(i);
                    }
                    else if (i.State != RemoteVolumeState.Verified)
                    {
                        database.UpdateRemoteVolume(i.Name, RemoteVolumeState.Verified, i.Size, i.Hash);
                    }

                    lookup.Remove(i.Name);
                }
            }
            
            return new RemoteAnalysisResult() { ParsedVolumes = remotelist, ExtraVolumes = lookup.Values, MissingVolumes = missing };
        }
        
        internal static IEnumerable<Volumes.IParsedVolume> ParseFileList(string target, Dictionary<string, string> options, CommunicationStatistics stat)
        {
            var opts = new Options(options);
            using (var db = new LocalDatabase(opts.Dbpath, "ParseFileList"))
            using (var b = new BackendManager(target, opts, stat, db))
            {
                var res = 
                    from n in b.List()
                    let np = Volumes.VolumeBase.ParseFilename(n)
                    where np != null
                    select np;
                    
                b.WaitForComplete(db, null);
                
                return res;
            }
        }    
    }    
}

