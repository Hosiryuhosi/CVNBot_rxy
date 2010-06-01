using System;
using System.Collections;
using System.Collections.Specialized;
using Meebey.SmartIrc4net;
using System.Threading;
using System.Text.RegularExpressions;
using log4net;

namespace SWMTBot
{
    struct RCEvent
    {
        public enum EventType
        {
            delete, restore, upload, block, unblock, edit, protect, unprotect,
            move, rollback, newuser, import, renameuser, makebot, unknown, newuser2
        }

        public string project;
        public string title;
        public string url;
        public string user;
        public bool minor;
        public bool newpage;
        public int szdiff;
        public string comment;
        public EventType eventtype;
        public string blockLength;
        public string movedTo;

        public override string ToString()
        {
            return "[" + project + "] " + user + " edited [[" + title + "]] (" + szdiff.ToString() + ") " + url + " " + comment;
        }
    }

    class RCReader
    {
        public IrcClient rcirc = new IrcClient();
        public DateTime lastMessage = DateTime.Now;
        public string lastError = "";

        // RC parsing regexen
        static Regex stripColours = new Regex(@"\x04\d{0,2}\*?");
        static Regex stripColours2 = new Regex(@"\x03\d{0,2}");
        static Regex stripBold = new Regex(@"\x02");
        static Regex fullString = new Regex(@"^\x03" + @"14\[\[\x03" + @"07(?<title>.+?)\x03" + @"14\]\]\x03" + @"4 (?<flag>.*?)\x03" + @"10 \x03" + @"02(?<url>.*)\x03 \x03" + @"5\*\x03 \x03" + @"03(?<user>.*?)\x03 \x03" + @"5\*\x03 (?<szdiff>.*?) \x03" + @"10(?<comment>.*)\x03$");
        static Regex rszDiff = new Regex(@"\(([\+\-])([0-9]+)\)");
        static Regex rflagMN = new Regex(@"[MN]{0,2}");

        private static ILog logger = LogManager.GetLogger("SWMTBot.RCReader");

        public void initiateConnection() {
            Thread.CurrentThread.Name = "RCReader";

            logger.Info("RCReader thread started");

            //Set up RCReader
            rcirc.Encoding = System.Text.Encoding.UTF8;
            rcirc.AutoReconnect = true;
            //rcirc.AutoRejoin = true;
            rcirc.OnChannelMessage += new IrcEventHandler(rcirc_OnChannelMessage);
            rcirc.OnConnected += new EventHandler(rcirc_OnConnected);
            rcirc.OnQueryMessage += new IrcEventHandler(rcirc_OnQueryMessage);

            try
            {
                rcirc.Connect("irc.wikimedia.org", 6667);
            }
            catch (ConnectionException e)
            {
                lastError = "Connection error: " + e.Message;
                return;
            }
            try
            {
                rcirc.Login(Program.botNick, "SWMTBot", 4, "SWMTBot");

                foreach (string prj in Program.prjlist.Keys)
                {
                    //logger.Info("Joining #" + prj);
                    rcirc.RfcJoin("#" + prj);
                }

                //Enter loop
                rcirc.Listen();
                rcirc.Disconnect();
            }
            catch (ConnectionException)
            {
                //Apparently this is handled, so we don't need to catch it
                return;
            }
        }

        void rcirc_OnQueryMessage(object sender, IrcEventArgs e)
        {
            //This is for the emergency restarter
            if (e.Data.Message == Program.botNick + ":" + (string)Program.mainConfig["botpass"] + " restart")
            {
                logger.Warn("Emergency restart ordered by " + e.Data.Nick);
                Program.PartIRC("Emergency restart ordered by " + e.Data.Nick);
                Program.Restart();
            }
        }

        void rcirc_OnConnected(object sender, EventArgs e)
        {
            logger.Info("Connected to live IRC feed");
        }

        void rcirc_OnChannelMessage(object sender, IrcEventArgs e)
        {
            lastMessage = DateTime.Now;

            //Same as RCParser.py->parseRCmsg()
            string strippedmsg = stripBold.Replace(stripColours.Replace(SWMTUtils.replaceStrMax(e.Data.Message, '\x03', '\x04', 14), "\x03"), "");
            string[] fields = strippedmsg.Split(new char[1] { '\x03' }, 15);
            if (fields.Length == 15)
            {
                if (fields[14].EndsWith("\x03"))
                    fields[14] = fields[14].Substring(0, fields[14].Length - 1);
            }
            else
            {
                //Console.WriteLine("Ignored: " + e.Data.Message);
                return; //Probably really long article title or something that got cut off; we can't handle these
            }
            //END

            try
            {
                RCEvent rce;
                rce.eventtype = RCEvent.EventType.unknown;
                rce.blockLength = "";
                rce.movedTo = "";
                rce.project = e.Data.Channel.Substring(1);
                rce.title = Project.translateNamespace(rce.project, fields[2]);
                rce.url = fields[6];
                rce.user = fields[10];
                //At the moment, fields[14] contains IRC colour codes. For plain edits, remove just the \x03's. For logs, remove using the regex.
                Match titlemo = ((Project)Program.prjlist[rce.project]).rSpecialLogRegex.Match(fields[2]);
                if (!titlemo.Success)
                {
                    //This is a regular edit
                    rce.minor = fields[4].Contains("M");
                    rce.newpage = fields[4].Contains("N");
                    rce.eventtype = RCEvent.EventType.edit;
                    rce.comment = fields[14].Replace("\x03", "");
                }
                else
                {
                    //This is a log edit; check for type
                    string logType = titlemo.Groups[1].Captures[0].Value;
                    //Fix comments
                    rce.comment = stripColours2.Replace(fields[14], "");
                    switch (logType)
                    {
                        case "newusers":
                            //Could be a user creating their own account, or a user creating a sockpuppet

                            //[[Special:Log/newusers]] create2  * Srikeit *  created account for User:Srikeit Test: [[User talk:Srikeit Test|Talk]] | [[Special:Contributions/Srikeit Test|contribs]] | [[Special:Blockip/Srikeit Test|block]]
                            //Check the flag
                            if (fields[4] == "create2")
                            {
                                Match mc2 = ((Project)Program.prjlist[rce.project]).rCreate2Regex.Match(rce.comment);
                                if (mc2.Success)
                                {
                                    rce.title = mc2.Groups[1].Captures[0].Value;
                                    rce.eventtype = RCEvent.EventType.newuser2;
                                }
                                else
                                {
                                    logger.Warn("Unmatched create2 event: " + rce.comment);
                                }
                            }
                            else
                                rce.eventtype = RCEvent.EventType.newuser;
                            break;
                        case "block":
                            //Could be a block or unblock; need to parse regex
                            Match bm = ((Project)Program.prjlist[rce.project]).rblockRegex.Match(rce.comment);
                            if (bm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.block;
                                rce.title = bm.Groups["item1"].Captures[0].Value;
                                rce.blockLength = "24 hours"; //Set default value in case our Regex has fallen back to laziness
                                try
                                {
                                    rce.blockLength = bm.Groups["item2"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                                try
                                {
                                    rce.comment = bm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match ubm = ((Project)Program.prjlist[rce.project]).runblockRegex.Match(rce.comment);
                                if (ubm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.unblock;
                                    rce.title = bm.Groups["item1"].Captures[0].Value;
                                    try
                                    {
                                        rce.comment = ubm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    //All failed; is block but regex does not match
                                    logger.Warn("Unmatched block type: " + rce.comment);
                                    return;
                                }
                            }
                            break;
                        case "protect":
                            //Could be a protect or unprotect; need to parse regex
                            Match pm = ((Project)Program.prjlist[rce.project]).rprotectRegex.Match(rce.comment);
                            if (pm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.protect;
                                rce.title = Project.translateNamespace(rce.project, pm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = pm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match upm = ((Project)Program.prjlist[rce.project]).runprotectRegex.Match(rce.comment);
                                if (upm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.unprotect;
                                    rce.title = Project.translateNamespace(rce.project, upm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = upm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched protect type: " + rce.comment);
                                    return;
                                }
                            }
                            break;
                        case "rights":
                            //Is rights
                            return; //Not interested today
                        //break;
                        case "delete":
                            //Could be a delete or restore; need to parse regex
                            Match dm = ((Project)Program.prjlist[rce.project]).rdeleteRegex.Match(rce.comment);
                            if (dm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.delete;
                                rce.title = Project.translateNamespace(rce.project, dm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = dm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match udm = ((Project)Program.prjlist[rce.project]).rrestoreRegex.Match(rce.comment);
                                if (udm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.restore;
                                    rce.title = Project.translateNamespace(rce.project, udm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = udm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched delete type: " + rce.comment);
                                    return;
                                }
                            }
                            break;
                        case "upload":
                            //Is an upload
                            Match um = ((Project)Program.prjlist[rce.project]).ruploadRegex.Match(rce.comment);
                            if (um.Success)
                            {
                                rce.eventtype = RCEvent.EventType.upload;
                                rce.title = Project.translateNamespace(rce.project, um.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = um.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                logger.Warn("Unmatched upload: " + rce.comment);
                                return;
                            }
                            break;
                        case "move":
                            //Is a move
                            rce.eventtype = RCEvent.EventType.move;
                            //Check "move over redirect" first: it's longer, and plain "move" may match both (e.g., en-default)
                            Match mrm = ((Project)Program.prjlist[rce.project]).rmoveredirRegex.Match(rce.comment);
                            if (mrm.Success)
                            {
                                rce.title = Project.translateNamespace(rce.project, mrm.Groups["item1"].Captures[0].Value);
                                rce.movedTo = Project.translateNamespace(rce.project, mrm.Groups["item2"].Captures[0].Value);
                                //We use the unused blockLength field to store our "moved from" URL
                                rce.blockLength = ((Project)Program.prjlist[rce.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(mrm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = mrm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match mm = ((Project)Program.prjlist[rce.project]).rmoveRegex.Match(rce.comment);
                                if (mm.Success)
                                {
                                    rce.title = Project.translateNamespace(rce.project, mm.Groups["item1"].Captures[0].Value);
                                    rce.movedTo = Project.translateNamespace(rce.project, mm.Groups["item2"].Captures[0].Value);
                                    //We use the unused blockLength field to store our "moved from" URL
                                    rce.blockLength = ((Project)Program.prjlist[rce.project]).rooturl + "wiki/" + SWMTUtils.wikiEncode(mm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = mm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched move type: " + rce.comment);
                                    return;
                                }
                            }
                            break;
                        case "import":
                            //Is an import
                            //rce.eventtype = RCEvent.EventType.import;
                            return; //Not interested today
                        //break;
                        case "renameuser":
                            //A user got renamed
                            //rce.eventtype = RCEvent.EventType.renameuser;
                            return; //Not interested today
                        //break;
                        case "makebot":
                            //New bot on the block
                            //rce.eventtype = RCEvent.EventType.makebot;
                            return; //Not interested today
                        //break;
                        default:
                            logger.Warn("Unhandled log type: " + logType + "; Comment was: " + rce.comment);
                            //Don't react to event
                            return;
                    }
                    //These flags don't apply to log events, but must be initialized
                    rce.minor = false;
                    rce.newpage = false;
                }

                //Deal with the diff size
                Match n = rszDiff.Match(fields[13]);
                if (n.Success)
                {
                    if (n.Groups[1].Captures[0].Value == "+")
                        rce.szdiff = Convert.ToInt32(n.Groups[2].Captures[0].Value);
                    else
                        rce.szdiff = 0 - Convert.ToInt32(n.Groups[2].Captures[0].Value);
                }
                else
                    rce.szdiff = 0;

                try
                {
                    Program.ReactToRCEvent(rce);
                }
                catch (Exception exce)
                {
                    Program.BroadcastDD("ERROR", "ReactorException", exce.Message, e.Data.Channel + " " + e.Data.Message);
                }
            }
            catch (ArgumentOutOfRangeException eor)
            {
                //Broadcast this for Distributed Debugging
                Program.BroadcastDD("ERROR", "RCR_AOORE", eor.Message, e.Data.Channel + " " + e.Data.Message);
            }
        }

    }
}
