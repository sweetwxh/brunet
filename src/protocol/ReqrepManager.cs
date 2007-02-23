/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2005  University of California

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

#define REQREP_DEBUG
using System;
using System.Collections;

namespace Brunet {
	
/**
 * This class manages the Request-Reply protocol
 * for semi-reliable Brunet messaging.
 *
 * By semi-reliable we mean that in most cases, packet loss or duplication
 * will not cause a problem, but it some cases (of extreme loss or delay)
 * a problem could remain.
 *
 * This protocol is useful for simple applications that only need a best
 * effort attempt to deal with lost packets.
 *
 * @todo implement adaptive timeouts
 */
	
public class ReqrepManager : IAHPacketHandler {
  
  public enum ReqrepType : byte
  {
    Request = 1, //A standard request that must be replied to at least once.
    LossyRequest = 2, //A request that does not require a response
    Reply = 3, //The response to a request
    Error = 6//Some kind of Error
  }

  public enum ReqrepError : byte
  {
    NoHandler = 1, //There is no handler for this protocol
    HandlerFailure = 2, //There is a Handler, but it could not reply.
    Timeout = 3 //This is a "local" error, there was no response before timeout
  }

  /**
   * Protected constructor, we want to control ReqrepManager instances
   * running on a node. 
   * @param node The Node we work for
   */
  protected ReqrepManager(Node node) {
    _node = node;
    _is_active = false;

    _sync = new Object();
    _rand = new Random();
    _req_handler_table = new Hashtable();
    _req_state_table = new Hashtable();
    _rep_handler_table = new Hashtable();
    _replies = new ArrayList();
    //resend the request after 5 seconds.
    _reqtimeout = new TimeSpan(0,0,0,0,5000);
    //Hold on to a reply for 50 seconds.
    ///@todo, we should also make sure to keep a maximum number of replies
    _reptimeout = new TimeSpan(0,0,0,0,50000);
    _last_check = DateTime.UtcNow;

    _node.ArrivalEvent += delegate(object n, EventArgs args) { 
#if REQREP_DEBUG
      Console.WriteLine("[ReqrepManager: {0}] Activated.",
			_node.Address);
#endif
      _is_active = true;
    };
    _node.DepartureEvent += delegate(object n, EventArgs args) { 
#if REQREP_DEBUG
      Console.WriteLine("[ReqrepManager: {0}] Deactivated.",
			_node.Address);
#endif
      _is_active = false;
    };

    //Subscribe on the node:
    _node.Subscribe(AHPacket.Protocol.ReqRep, this);
    _node.HeartBeatEvent += new EventHandler(this.TimeoutChecker);

  }

  /** static hashtable to keep track of ReqrepManager objects. */
  protected static Hashtable _rrm_table  = new Hashtable();
  /** static lock for protecting the Hashtable above. */
  protected static object _class_lock = new object();
      
  /** 
   * Static method to create ReqrepManager objects
   * @param node The node we work for
   */
  public static ReqrepManager GetInstance(Node node) {
    lock(_rrm_table) {
      //check if there is already an instance object for this node
      if (_rrm_table.ContainsKey(node)) {
	return (ReqrepManager) _rrm_table[node];
      }
      //in case no instance exists, create one
      ReqrepManager rrm  = new ReqrepManager(node); 
      _rrm_table[node] = rrm;
      return rrm;
    }
  }

  public class Statistics {
    public int SendCount;
    public Statistics() {
    }
  }
   /**
    * This is an inner class used to keep track
    * of all the information for a request
    */
   protected class RequestState {
     public RequestState() {
       Timeouts = 6;
     }

     public int Timeouts;
     public IReplyHandler ReplyHandler;
     public DateTime ReqDate;
     public AHPacket Request;
     public ReqrepType RequestType;
     public int RequestID;
     public object UserState;
     //this is something we need to get rid of 
     //public bool Replied;
     //number of times request has been sent out
     public int SendCount;
   }
   protected class ReplyState {
     public int RequestID;
     public AHPacket Reply;
     public DateTime RepDate;
     public AHPacket Request;
     public string PayloadType;
   }
   // Member variables:

   protected Node _node;
   public Node Node { get { return _node; } }

   protected volatile bool _is_active;

   protected object _sync;
   protected Random _rand;
   protected Hashtable _req_state_table;
   protected ArrayList _replies;
   protected Hashtable _rep_handler_table;
   protected Hashtable _req_handler_table;
   protected TimeSpan _reptimeout;
   protected TimeSpan _reqtimeout;
   //This is to keep track of when we looked for timeouts last
   protected DateTime _last_check;
   // Methods /////


   public static void DebugPacket(AHAddress local, AHPacket p, Edge from) {
     System.IO.MemoryStream ms = p.PayloadStream;
     ReqrepType rt = (ReqrepType)((byte)ms.ReadByte());
     int idnum = NumberSerializer.ReadInt(ms);
     if ( rt == ReqrepType.Request || rt == ReqrepType.LossyRequest ) {
       Console.WriteLine("[ReqrepManager Debug Hook: {0}] Request id: {1} from node: {2}",
			 local, idnum, p.Source);
     } else if ( rt == ReqrepType.Reply) {
       Console.WriteLine("[ReqrepManager Debug Hook: {0}] Reply id: {1} from node: {2}",
			 local, idnum, p.Source);       
     } else {
       Console.WriteLine("[ReqrepManager Debug Hook: {0}] Unknown id: {1} from node: {2}",
			 local, idnum, p.Source);              
     }
   }
   /**
    * This is either a request or response.  Look up the handler
    * for it, and pass the packet to the handler
    */
   public void HandleAHPacket(object node, AHPacket p, Edge from) {
     if (!_is_active) {
#if REQREP_DEBUG
       Console.WriteLine("[ReqrepManager: {0}] Inactive. Simply return (HandleAHPacket).",
			 _node.Address);
#endif
       return;
     }
#if REQREP_DEBUG
     Console.WriteLine("[ReqrepManager: {0}] Receiving packet at: {1}.", _node.Address, DateTime.Now);
#endif

     //Simulate packet loss
     //if ( _rand.NextDouble() < 0.1 ) { return; }
     //Is it a request or reply?
     System.IO.MemoryStream ms = p.PayloadStream;
     ReqrepType rt = (ReqrepType)((byte)ms.ReadByte());
     int idnum = NumberSerializer.ReadInt(ms);
     if( rt == ReqrepType.Request || rt == ReqrepType.LossyRequest ) {
       int count = 0;
       string pt = NumberSerializer.ReadString(ms, out count);
       /**
	* Lets see if we have been asked this question before
	*/
       AHPacket error = null;
       IRequestHandler irh = null;
       ReplyState rs = null;
       bool start_new_rh = false;
       lock( _sync ) {
	 foreach(ReplyState repstate in _replies) {
	   if( repstate.RequestID == idnum &&
	     repstate.Request.Source.Equals( p.Source ) ) {
	     //This is old news
	     rs = repstate;
	     repstate.RepDate = DateTime.UtcNow;
	     break;
	   }
	 }
#if REQREP_DEBUG
	 Console.WriteLine("[ReqrepServer: {0}] Receiving request id: {1} from node: {2}",
			   _node.Address, idnum, p.Source);
#endif



	 if ( rs == null ) {
#if REQREP_DEBUG
	   Console.WriteLine("[ReqrepServer: {0}] This is a new request, actually invoke handler",
			     _node.Address);
#endif



	   //Looks like we need to handle this request
	   //Make a new ReplyState:
	   rs = new ReplyState();
	   rs.RequestID = idnum;
	   rs.Request = p;
	   rs.PayloadType = pt;
	   rs.Reply = null;
	   //Add the new reply state before we drop the lock
	   _replies.Add(rs);
	   start_new_rh = true;
	   irh = (IRequestHandler)_req_handler_table[pt];
	 }
       }//Drop the lock

       if( start_new_rh ) {
	 if( irh == null ) {
#if REQREP_DEBUG
	   Console.WriteLine("[ReqrepServer: {0}] No handler found for request.",
			     _node.Address);
#endif
	   //We have no handler
	   short ttl = _node.DefaultTTLFor( p.Source );
	   error = MakeError(p.Source, ttl, idnum, ReqrepError.NoHandler);
	 }
	 else {
	   try {
	     /*
	      * When this request is finishes, the method SendReply
	      * is called
	      *
	      * Skip over the 1 byte that says what kind of request,
	      * 4 byte request id, and the length of the protocol type
	      */
	     System.IO.MemoryStream offsetpayload = p.GetPayloadStream(5 + count);
#if REQREP_DEBUG
	     Console.WriteLine("[ReqrepServer: {0}] Calling appropriate handler.", 
			       _node.Address);
#endif
	     irh.HandleRequest(this,rt,rs,pt,offsetpayload,p);
	   }
	   catch(Exception) {
#if REQREP_DEBUG
	     Console.WriteLine("[ReqrepServer: {0}] Something went wrong in request handler.", 
			       _node.Address);
#endif
	     //Something has gone wrong
	     short ttl = _node.DefaultTTLFor( p.Source );
	     error = MakeError(p.Source, ttl, idnum,
				  ReqrepError.HandlerFailure);
	   }
	 }
       } 
       //Now just send this reply
       if( error == null ) {
#if REQREP_DEBUG
	 Console.WriteLine("[ReqrepServer: {0}] No.error.",
			   _node.Address, idnum, p.Source);
#endif
	 if( rs.Reply != null ) {
#if REQREP_DEBUG
	   Console.WriteLine("[ReqrepServer: {0}] Sending reply to {1}",
			     _node.Address, p.Source);
#endif
	   _node.Send( rs.Reply );
	 }
       }
       else {
	 /*
	  * We only send an error in the case that we were exactly
	  * the destination.  This prevents multiple errors coming
	  * back for one particular request.
	  */
	 if( p.Destination.Equals( _node.Address ) ) {
#if REQREP_DEBUG
	   Console.WriteLine("[ReqrepServer: {0}] Sending error to {1}. ",
			   _node.Address, p.Source);
#endif
	   _node.Send( error );
	 }
       }
     }
     else if( rt == ReqrepType.Reply ) {
       int count = 0;
       string pt = NumberSerializer.ReadString(ms, out count);
       lock( _sync ) {
	 RequestState reqs = (RequestState)_req_state_table[idnum];
	 if( reqs != null ) {
	   System.IO.MemoryStream offsetpayload = p.GetPayloadStream(5 + count);
	   Statistics statistics = new Statistics();
	   statistics.SendCount = reqs.SendCount;
	   bool continue_listening = 
		   reqs.ReplyHandler.HandleReply(this, rt, idnum, pt,
						 offsetpayload, p, statistics, reqs.UserState);
#if REQREP_DEBUG
      Console.WriteLine("[ReqrepManager: {0}] Contine listening on request is: {1}, is: {2}", 
			_node.Address, idnum, continue_listening);
#endif
	   //the request has been served
	   //reqs.Replied = true;
	   if( !continue_listening ) {
	     //Now remove the RequestState:
	     _req_state_table.Remove(idnum);
	   }
	 }
       }
       //Now we ar done.  We have already handled this Reply
     }
     else if( rt == ReqrepType.Error ) {
       ReqrepError rrerr = (ReqrepError)( (byte)ms.ReadByte() );
       lock( _sync ) {
	 //Get the request:
	 RequestState reqs = (RequestState)_req_state_table[idnum];
	 if( reqs != null ) {
	   if( reqs.Request.Destination.Equals( p.Source ) ) {
	     //This error really came from the node we sent to
	     reqs.ReplyHandler.HandleError(this, idnum, rrerr, reqs.UserState);
	     _req_state_table.Remove(idnum); 
	   }
	 }
	 else {
	   //We have already dealt with this Request
	 }
       }
     }
   }

   protected AHPacket MakeError(Address destination,
				 short ttl,
				 int next_rep,
				 ReqrepError err)
   {
     /*
      * 1 byte for the request type (error)
      * 4 byte integer ID
      * 1 byte for the type of Error
      */
     byte[] req_payload = new byte[ 1 + 4 + 1 ];
     req_payload[0] = (byte)ReqrepType.Error;
     NumberSerializer.WriteInt( next_rep, req_payload, 1 );
     req_payload[5] = (byte)err;
     AHPacket packet = new AHPacket(0, ttl, _node.Address, destination, AHPacket.AHOptions.Exact,
				      AHPacket.Protocol.ReqRep, req_payload);
     return packet;
   }

   protected AHPacket MakePacket (Address destination,
				 short ttl,
				 ReqrepType rt,
				 int next_rep,
				 string prot,
				 ushort options,
				 MemBlock payload)
   {
       //Here we make the payload while we will send:
       /**
	* The format is:
	* 1 byte to give the type of request/reply
	* 4 byte integer ID
	* protocol_len bytes type of payload
	* payload.Length bytes for the payload
	*/
       int protocol_len = NumberSerializer.GetByteCount(prot);
       byte[] req_payload = new byte[ 1 + 4 + protocol_len + payload.Length ];
       int offset = 0;
       req_payload[offset] = (byte)rt;
       offset += 1;
       NumberSerializer.WriteInt( next_rep, req_payload, 1 );
       offset += 4;
       offset += NumberSerializer.WriteString(prot, req_payload, offset);
       payload.CopyTo(req_payload, offset);
       AHPacket packet = new AHPacket(0, ttl, _node.Address, destination, options,
				      AHPacket.Protocol.ReqRep, req_payload);
       return packet;
   }
  /**
   * @deprecated use the one with a MemBlock instead
   */
  public int SendRequest(Address destination, ReqrepType reqt,
                         string prot,
		         byte[] payload, IReplyHandler reply, object state)
  {
    MemBlock pl = MemBlock.Reference(payload, 0, payload.Length);
    return SendRequest(destination, reqt, prot, pl, reply, state);
  }
  /**
   * @param destination the Node to recieve the request
   * @param prot the protocol of the payload
   * @param payload the Payload to send
   * @param reqt the type of request to make
   * @param reply the handler to handle the reply
   * @param state some state object to attach to this request
   * @return the identifier for this request
   *
   */
  public int SendRequest(Address destination, ReqrepType reqt,
                         string prot,
		         MemBlock payload, IReplyHandler reply, object state)
  {
    if (!_is_active) {
#if REQREP_DEBUG
      Console.WriteLine("[ReqrepManager: {0}] Inactive. Simply return (SendRequest).",
			_node.Address);
#endif
      //we are no longer active
      return -1;
    }
    if ( reqt != ReqrepType.Request && reqt != ReqrepType.LossyRequest ) {
      throw new Exception("Not a request");
    }
    RequestState rs = new RequestState();
    lock( _sync ) {
      //Get the index 
      int next_req = _rand.Next();
      do {
        next_req = _rand.Next();
      } while( _req_state_table.ContainsKey( next_req ) );
      /*
       * Now we store the request
       */
      rs.RequestID = next_req;
      short ttl = _node.DefaultTTLFor(destination);
      rs.ReplyHandler = reply;
      rs.Request = MakePacket(destination, ttl, reqt, next_req, prot,
                              AHPacket.AHOptions.AddClassDefault, payload);
      rs.ReqDate = DateTime.UtcNow;
      rs.RequestType = reqt;
      rs.UserState = state;
      //rs.Replied = false;
      rs.SendCount = 1;
      _req_state_table[ next_req ] = rs;
    }
#if REQREP_DEBUG
    Console.WriteLine("[ReqrepClient: {0}] Sending a request: {1} to node: {2}",
		      _node.Address, rs.RequestID, destination);
#endif

    _node.Send( rs.Request );
    return rs.RequestID;
  }
  /**
   * @deprecated use the one that takes a MemBlock
   */
  public int SendExactRequest(Address destination, ReqrepType reqt,
                         string prot,
		         byte[] payload, IReplyHandler reply, object state)
  {
    MemBlock pl = MemBlock.Reference(payload, 0, payload.Length);
    return SendExactRequest(destination, reqt, prot, pl, reply, state);
  }
  /**
   * @param destination the Node to recieve the request (this operates in Exact routing mode).
   * @param prot the protocol of the payload
   * @param payload the Payload to send
   * @param reqt the type of request to make
   * @param reply the handler to handle the reply
   * @param state some state object to attach to this request
   * @return the identifier for this request
   *
   */
  public int SendExactRequest(Address destination, ReqrepType reqt,
                         string prot,
		         MemBlock payload, IReplyHandler reply, object state)
  {
    if (!_is_active) {
#if REQREP_DEBUG
      Console.WriteLine("[ReqrepManager: {0}] Inactive. Simply return (SendRequest).",
			_node.Address);
#endif
      //we are no longer active
      return -1;
    }
    if ( reqt != ReqrepType.Request && reqt != ReqrepType.LossyRequest ) {
      throw new Exception("Not a request");
    }
    RequestState rs = new RequestState();
    lock( _sync ) {
      //Get the index 
      int next_req = _rand.Next();
      do {
        next_req = _rand.Next();
      } while( _req_state_table.ContainsKey( next_req ) );
      /*
       * Now we store the request
       */
      rs.RequestID = next_req;
      short ttl = _node.DefaultTTLFor(destination);
      rs.ReplyHandler = reply;
      rs.Request = MakePacket(destination, ttl, reqt, next_req, prot,
                              AHPacket.AHOptions.Exact, payload);
      rs.ReqDate = DateTime.UtcNow;
      rs.RequestType = reqt;
      rs.UserState = state;
      //rs.Replied = false;
      rs.SendCount = 1;
      _req_state_table[ next_req ] = rs;
    }
#if REQREP_DEBUG
    Console.WriteLine("[ReqrepClient: {0}] Sending a request: {1} to node: {2}",
		      _node.Address, rs.RequestID, destination);
#endif

    _node.Send( rs.Request );
    return rs.RequestID;
  }
  /**
   * @deprecated use the one that takes a MemBlock
   */
  public void SendReply(object request, byte[] response)
  {
    MemBlock resp = null;
    if( response != null ) {
      resp = MemBlock.Reference(response, 0, response.Length);
    }
    SendReply(request, resp );
  }
  /**
   * @param request this object allows the manager to know what Request this is a response to
   * @param response the payload for the response.
   */
  public void SendReply(object request, MemBlock response)
  {
    if (!_is_active) {
      //ignore!
#if REQREP_DEBUG
      Console.WriteLine("[ReqrepManager: {0}] Inactive. Simply return (SendReply).",
			_node.Address);
#endif
      return;
    }
    ReplyState rs = (ReplyState)request;
    AHPacket p = rs.Request;
    if( response != null ) {
      short ttl = _node.DefaultTTLFor( p.Source );
      rs.Reply = MakePacket(p.Source, ttl, ReqrepType.Reply, rs.RequestID,
                            rs.PayloadType, AHPacket.AHOptions.Exact, response);
    }
    else {
      /**
       * A Null reply means don't send any reply.
       * This is only allowed for LossyRequests
       */
      rs.Reply = null;
    }
    rs.RepDate = DateTime.UtcNow;
    lock( _sync ) {
      _replies.Add( rs );
    }
    if( rs.Reply != null ) {
      _node.Send( rs.Reply );
    }
  }
  
  /**
   * For a given protocol (wrapped inside the request/reply) send
   * the request to the given IRequestHandler
   * 
   * Note that we use the term "Bind" to emphasize that only one
   * IRequestHandler may be Binded to a particular protocol.
   *
   * @throws Exception if there is another IRequestHandler already Binded.
   */
  public void Bind(string p, IRequestHandler reqh) {
    lock( _sync ) {
      if( _req_handler_table.ContainsKey(p) ) {
        //Someone is already bound
        throw new Exception("Cannot bind to protocol: " + p.ToString());
      }
      else {
        _req_handler_table[p] = reqh;
      }
    }
  }

  /**
   * This method listens for the HeartBeatEvent from the
   * node and checks for timeouts.
   */
  protected void TimeoutChecker(object node, EventArgs args)
  {
    DateTime now = DateTime.UtcNow;
    if( now - _last_check > _reqtimeout ) {
      //Here is a list of all the handlers for the requests that timed out
      ArrayList timeout_hands = new ArrayList();
      ArrayList to_resend = new ArrayList();
      lock( _sync ) {
        _last_check = now;
        IDictionaryEnumerator reqe = _req_state_table.GetEnumerator();
        while( reqe.MoveNext() ) {
          RequestState reqs = (RequestState)reqe.Value;
          if( now - reqs.ReqDate > _reqtimeout ) {
            reqs.Timeouts--;
            if( reqs.Timeouts >= 0 ) {
              //Resend:
              if( reqs.RequestType != ReqrepType.LossyRequest) {
                //We don't resend LossyRequests
		//if (!reqs.Replied) {
		//requests are now resent no matter what
		reqs.ReqDate = now;
		reqs.SendCount++;
		to_resend.Add( reqs.Request );
		//}
              }
            }
            else {
              //We have timed out.
              timeout_hands.Add( reqs ); 
            }
          }
        }
        //Clean up the req_state_table:
        foreach(RequestState reqs in timeout_hands) {
          _req_state_table.Remove( reqs.RequestID );
        }
        //Look for any Replies it might be time to clean:
        ArrayList timedout_replies = new ArrayList();
        foreach(ReplyState reps in _replies) {
          if( now - reps.RepDate > _reptimeout ) {
            timedout_replies.Add( reps );
          }
        }
        foreach(ReplyState reps in timedout_replies) {
          _replies.Remove(reps);
        }
      }
      /*
       * It is important not to hold the lock while we call
       * functions that could result in this object being
       * accessed.
       *
       * We have released the lock, now we can send the packets:
       */
      foreach(Packet p in to_resend) {
        _node.Send(p);
      }
      /*
       * Once we have released the lock, tell the handlers
       * about the timeout that have occured
       */
      foreach(RequestState reqs in timeout_hands) {
        reqs.ReplyHandler.HandleError(this, reqs.RequestID,
                                      ReqrepError.Timeout, reqs.UserState);
      }
    }
  }
  /**
   * For a given protocol (wrapped inside the request/reply) stop sending
   * the requests to the given RequestHandler
   *
   * Unbind the given IRequestHandler.
   * @throws Exception if the given IRequestHandler is not bound to the protocol.
   */
  public void Unbind(string p, IRequestHandler reqh) {
    lock( _sync ) {
      object o = _req_handler_table[p];
      if( o != reqh ) {
        throw new Exception("Not bound to protocol");
      }
      else {
        _req_handler_table.Remove(p);
      }
    }
  }
}
  
}
