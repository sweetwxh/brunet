/*
Copyright (C) 2010 David Wolinsky <davidiw@ufl.edu>, University of Florida

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

using Brunet.Messaging;
using Brunet.Transport;
using Brunet.Util;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Brunet.Symphony {
  /// <summary>Create edges using another overlay as the transport</summary>
  public class SubringEdgeListener : EdgeListener, IDataHandler {
    protected readonly IdentifierTable _it;
    protected readonly TransportAddress _local_ta;
    // We don't want to advertise this Address!
    protected static readonly ArrayList _local_tas = new ArrayList(0);
    protected readonly Node _private_node;
    protected int _running;
    protected readonly Node _shared_node;
    protected int _started;
    protected readonly PType _ptype;

    public override int Count { get { return _it.Count; } }
    public override bool IsStarted { get { return _started == 1; } }
    public override IEnumerable LocalTAs { get { return _local_tas; } }
    public override TransportAddress.TAType TAType {
      get {
        return TransportAddress.TAType.Subring;
      }
    }

    /// <summary>Create a SubringEdgeListener.</summary>
    /// <param name="shared_node">The overlay used for the transport.</param>
    /// <param name="private_node">The overlay needing edges.</param>
    public SubringEdgeListener(Node shared_node, Node private_node)
    {
      _shared_node = shared_node;
      _private_node = private_node;
      _it = new IdentifierTable();

      _local_ta = new SubringTransportAddress(shared_node.Address as AHAddress,
          shared_node.Realm);

      _ptype = new PType("ns:" + shared_node.Realm);
      shared_node.DemuxHandler.GetTypeSource(_ptype).Subscribe(this, null);

      _running = 0;
      _started = 0;
    }

    /// <summary>Remove closed edges from the IdentifierTable</summary>
    protected void CloseHandler(object o, EventArgs ea)
    {
      SubringEdge se = o as SubringEdge;
      if(se != null) {
        _it.Remove(se.LocalID);
      }
    }

    public override void CreateEdgeTo(TransportAddress ta, EdgeCreationCallback ecb)
    {
      SubringTransportAddress sta = ta as SubringTransportAddress;

      if(sta == null) {
        ecb(false, null, new Exception("TA Type is not Subring!"));
      } else if(!sta.Namespace.Equals(_shared_node.Realm)) {
        ecb(false, null, new Exception("Namespace mismatch"));
      } else if(sta.Target.Equals(_private_node.Address)) {
        ecb(false, null, new Exception("You are me!"));
      } else {
        SubringEdge se = new SubringEdge(_local_ta, sta, false,
            new AHExactSender(_shared_node, sta.Target), _ptype);
        se.CloseEvent += CloseHandler;
        _it.Add(se);
        ecb(true, se, null);
      }
    }

    /// <summary>Where data packets prepended with a prepended subring come.
    /// Here we receive data as well as create new SubringEdges.</summary>
    public void HandleData(MemBlock data, ISender return_path, object state)
    {
      AHSender from = return_path as AHSender;
      if(from == null) {
        return;
      }

      AHAddress target = (AHAddress) from.Destination;
      MemBlock payload;
      int local_id, remote_id;
      _it.Parse(data, out payload, out local_id , out remote_id);

      IIdentifierPair ip;
      SubringEdge se;

      if(_it.TryGet(local_id, remote_id, out ip)) {
        se = ip as SubringEdge;
      } else if(local_id == 0) {
        if(!from.Node.Realm.Equals(_shared_node.Realm)) {
          // We don't have matching realms
          return;
        }
        var rem_sta = new SubringTransportAddress(target, from.Node.Realm);
        se = new SubringEdge(_local_ta, rem_sta, true, from, _ptype);
        _it.Add(se);
        se.RemoteID = remote_id;
        se.CloseEvent += CloseHandler;
        SendEdgeEvent(se);
      } else {
        // Probably an edge closed earlier...
        return;
      }

      se.ReceivedPacketEvent(payload);
    }

    public override void Start()
    {
      if(Interlocked.Exchange(ref _started, 1) == 1) {
        throw new Exception("SubringEdgeListener cannot be started twice.");
      }

      Interlocked.Exchange(ref _running, 1);
    }

    public override void Stop()
    {
      Interlocked.Exchange(ref _running, 0);
      base.Stop();
    }
  }
}
