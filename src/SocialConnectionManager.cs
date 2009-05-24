/*
Copyright (C) 2009 Pierre St Juste <ptony82@ufl.edu>, University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.IO;

using Brunet;

#if SVPN_NUNIT
using NUnit.Framework;
#endif

namespace SocialVPN {

  /**
   * This class is in charge of making connections between friends. It
   * manages the social networking backends as well as the identity providers
   * of the system.
   */
  public class SocialConnectionManager {

    /**
     * The start time for the timer thread (30 secs).
     */
    private const int STARTTIME = 30000;

    /**
     * The interval time for the timer thread (5 mins).
     */
    private const int INTERVALTIME = 300000;

    /**
     * The node which accepts peers based on certificates.
     */
    protected readonly SocialNode _snode;

    /**
     * The social network and identity provider.
     */
    protected readonly SocialNetworkProvider _snp;

    /** 
     * The HTTP interface to manage socialvpn.
     */
    protected readonly HttpInterface _http;

    /**
     * The handles RPC for socialvpn.
     */
    protected readonly SocialRpcHandler _srh;

    /**
     * Timer thread.
     */
    protected readonly Timer _timer_thread;

    /**
     * Constructor.
     * @param node the social node.
     * @param provider the identity provider.
     * @param network the social network.
     * @param port the port number for the HTTP interface.
     * @param srh the social rpc handler.
     */
    public SocialConnectionManager(SocialNode node,SocialNetworkProvider snp,
                                   SocialRpcHandler srh, string port) {
      _snode = node;
      _snp = snp;
      _http = new HttpInterface(port);
      _http.ProcessEvent += ProcessHandler;
      _http.Start();
      _srh = srh;
      _srh.SyncEvent += SyncHandler;
      _timer_thread = new Timer(new TimerCallback(TimerHandler), null,
                                STARTTIME, INTERVALTIME);
    }

    /**
     * Timer event handler.
     * @param obj the default object.
     */
    public void TimerHandler(Object obj) {
      ProtocolLog.WriteIf(SocialLog.SVPNLog, "TIMER HANDLER CALL: " +
                        DateTime.Now.Second + "." +
                        DateTime.Now.Millisecond + " " +
                        DateTime.UtcNow);
      try {
        UpdateFriends();
        _snp.StoreFingerprint();
        _snode.PublishCertificate();
        _srh.PingFriends();
        _timer_thread.Change(INTERVALTIME, INTERVALTIME);
      } catch (Exception e) {
        if(e.Message.StartsWith("Dht")) {
          // Only change time on Dht not activitated
          _timer_thread.Change(STARTTIME, STARTTIME);
        }
        ProtocolLog.WriteIf(SocialLog.SVPNLog, e.Message);
        ProtocolLog.WriteIf(SocialLog.SVPNLog, "TIMER HANDLER FAILURE: " +
                          DateTime.Now.Second + "." +
                          DateTime.Now.Millisecond + " " +
                          DateTime.UtcNow);
      }
    }

    /**
     * Process event handler.
     * @param obj the default object.
     * @param eargs the event arguments.
     */
    public void ProcessHandler(Object obj, EventArgs eargs) {
      Dictionary<string, string> request = (Dictionary<string,string>) obj;
      if(request.ContainsKey("m")) {
        switch(request["m"]) {
          case "add":
            _snp.AddFriends(request["uids"]);;
            UpdateFriends();
            break;

          case "addfpr":
            _snp.AddFingerprints(request["fprs"]);
            UpdateFriends();
            break;

          case "addcert":
            _snp.AddCertificate(request["cert"]);
            UpdateFriends();
            break;

          case "login":
            _snp.Login(request["id"], request["user"], request["pass"]);
            UpdateFriends();
            break;
            
          case "allow":
            AllowFriends(request["fprs"]);
            break;

          case "block":
            BlockFriends(request["fprs"]);
            break;

          default:
            break;
        }
      }
      request["response"] = _snode.GetState();
    }

    /**
     * Sync event handler.
     * @param obj the default object.
     * @param eargs the event arguments.
     */
    public void SyncHandler(Object obj, EventArgs eargs) {
      string dht_key = (string) obj;
      string[] parts = dht_key.Split(':');
      string uid = parts[1];

      if(_snp.GetFriends().Contains(uid)) {
        AddFriends(new string[] {uid});
      }
    }

    /**
     * Updates friends and adds to socialvpn.
     */
    protected void UpdateFriends() {
      AddFriends(_snp.GetFriends().ToArray());
    }

    /**
     * Allow a list of fingerprints seperated by newline.
     * @param fprlist a list of fingerprints.
     */
    protected void AllowFriends(string fprlist) {
      string[] fprs = fprlist.Split('\n');
      foreach(string fpr in fprs) {
        _snode.AddFriend(fpr);
      }
    }

    /**
     * Block a list of fingerprints seperated by newline.
     * @param fprlist a list of fingerprints.
     */
    protected void BlockFriends(string fprlist) {
      string[] fprs = fprlist.Split('\n');
      foreach(string fpr in fprs) {
        _snode.RemoveFriend(fpr);
      }
    }

    /**
     * Adds a list of friend based on user id.
     * @param uids the list of friend's user id.
     */
    protected void AddFriends(string[] uids) {
      List<byte[]> certificates = _snp.GetCertificates(uids);
      foreach(byte[] cert in certificates) {
        _snode.AddCertificate(cert);
      }
      List<string> fingerprints = _snp.GetFingerprints(uids);
      foreach(string fpr in fingerprints) {
        _snode.AddDhtFriend(fpr);
      }
    }
  }

#if SVPN_NUNIT
  [TestFixture]
  public class SocialConnectionManagerTester {
    [Test]
    public void SocialConnectionManagerTest() {
      Assert.AreEqual("test", "test");
    }
  } 
#endif
}
