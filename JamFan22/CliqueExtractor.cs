/*
using System;
using System.IO;
using System.Collections.Generic;
using CsvHelper;
using System.IO;
using System.Formats.Asn1;
using System.Globalization;

public class PersonOnServerAtTime
{
    public string Person { get; set; }
    public string Server { get; set; }
    public string Time { get; set; }
}

public class GroupEvent
{
    // constructor that creates the People HashSet object
    public GroupEvent()
    {
        People = new HashSet<string>();
    }
    public HashSet<string> People { get; set; }
    public string Server { get; set; }
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    public int Duration { get { return EndMinute - StartMinute; } }
}

public class FindPatterns
{
    public static void Main()
    {
        // Create a reader object and read the CSV file into a list of objects
        var reader = new StreamReader("c:\\users\\user\\trimmed_census.csv");

        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            var records = csv.GetRecords<PersonOnServerAtTime>().ToList();

            // Now create GroupTogether objects
            var groups = new List<GroupEvent>();

            // Sort by server, then by minute

            records.Sort((x, y) => x.Server.CompareTo(y.Server));
            records.Sort((x, y) => x.Time.CompareTo(y.Time));

            // all GroupEvents begin on the first minute they're noticed, as one-minute events.
            // Then if they still exist in the next minute, their EndMinute rises.
            // This ends when the People lose at least one member.

            // The quick-dirty way to start this is a dictionary of time+server keys, filled by people.
            Dictionary<string, HashSet<string>> protoClique = new Dictionary<string, HashSet<string>>();
            foreach (var record in records)
            {
                string key = record.Time + record.Server;
                if (protoClique.ContainsKey(key))
                {
                    protoClique[key].Add(record.Person);
                }
                else
                {
                    protoClique.Add(key, new HashSet<string>() { record.Person });
                }
            }

            // Finally, create GroupEvents based on these one-minute cliques.

            foreach (var key in protoClique.Keys)
            {
                var group = new GroupEvent();
                group.Server = key.Substring(6);
                group.StartMinute = Int32.Parse(key.Substring(0, 6));
                group.EndMinute = Int32.Parse(key.Substring(0, 6));
                group.People = protoClique[key];
                groups.Add(group);
            }

            // ok, for each group in groups, extend its endminute as long as the people remain

            foreach (var group in groups)
            {
                bool stillTogether = true;
                while (stillTogether)
                {
                    int nextMinute = group.EndMinute + 1;
                    string nextKey = nextMinute.ToString() + group.Server;
                    if (protoClique.ContainsKey(nextKey))
                    {
                        // check if all the people in the group are in the next minute's clique
                        foreach (var person in group.People)
                        {
                            if (false == protoClique[nextKey].Contains(person))
                            {
                                stillTogether = false;
                            }
                        }
                        if (stillTogether)
                        {
                            group.EndMinute = nextMinute;
                        }
                    }
                    else
                    {
                        stillTogether = false;
                    }
                }
            }

            // Now tell me about the big groups that had long runs.
            // Sort by group size times duration (in minutes)

            groups.Sort((x, y) => (y.People.Count * y.Duration).CompareTo(x.People.Count * x.Duration));
            foreach (var group in groups)
            {
                if (group.People.Count > 2)
                    if (group.Duration > 10)
                        Console.WriteLine("Server: " + group.Server + " Start: " + group.StartMinute + " Size: " + group.People.Count + " Duration: " + (group.Duration));
            }

            Console.WriteLine("That's all fokls.");
        }
    }
}



// Load c:\users\user\trimmed_server.csv
// Load c:\users\user\trimmed_censusgeo.csv

*/