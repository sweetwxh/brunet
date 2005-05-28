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

/*
 * Dependencies : 
 Brunet.Address
 Brunet.AddressParser
 Brunet.ConnectionMessage
 Brunet.ConnectionType
 Brunet.TransportAddress
 */

using System.Xml;
using System.Collections;
#if BRUNET_NUNIT
using NUnit.Framework;
#endif

namespace Brunet
{

  /**
   * The ConnectionMessage that is sent out on the network
   * to request connections be made to the sender.
   *
   * When a Node sends out a ConnectToMessage, it puts
   * itself as the target.  This is because that node
   * is requesting that the recipient of the ConnectToMessage
   * connect to the sender (thus the sender is the target).
   *
   * When a node recieves a ConnectToMessage, the CtmRequestHandler
   * processes the message.  ConnectToMessages are sent by
   * Connector objects.
   *
   * This object is immutable
   * 
   * @see CtmRequestHandler
   * @see Connector
   */
  public class ConnectToMessage:ConnectionMessage
  {

    /**
     * @param t connection type
     * @param target the Address of the target node
     */
    public ConnectToMessage(ConnectionType t, NodeInfo target)
    {
      _ct = Connection.ConnectionTypeToString(t);
      _target_ni = target;
    }
    public ConnectToMessage(string contype, NodeInfo target)
    {
      _ct = contype;
      _target_ni = target;
    }
    public ConnectToMessage(string contype, NodeInfo target, NodeInfo[] neighbors)
    {
      _ct = contype;
      _target_ni = target;
      _neighbors = neighbors;
    }
    /**
     * Prefer this constructor
     * @param t ConnectionType for this message
     * @param target the Address of the Node to connect to
     * @param tas the TransportAddresses to connect to in order of preference
     */
    public ConnectToMessage(ConnectionType t, Address target, TransportAddress[] tas)
    {
      _ct = Connection.ConnectionTypeToString(t);
      _target_ni = new NodeInfo(target, new ArrayList(tas));
    }
    /**
     * This constructor wraps the above constructor
     * @param t ConnectionType for this message
     * @param target the Address of the Node to connect to
     * @param tas the TransportAddresses to connect to in order of preference
     */
    public ConnectToMessage(ConnectionType t, Address target, ICollection tas)
    {
      _ct = Connection.ConnectionTypeToString(t);
      _target_ni = new NodeInfo(target, new ArrayList(tas));
    }
    /**
     * Deserializes the whole <request />
     */
    public ConnectToMessage(System.Xml.XmlElement r) : base(r)
    {
      XmlElement encoded = (XmlElement)r.FirstChild;
      //Read the attributes of the connectTo
      foreach(XmlNode attr in((XmlElement) encoded).Attributes)
      {
        switch (attr.Name) {
        case "type":
          _ct = attr.FirstChild.Value;
          break;
        }
      }
      //Read the children
      foreach(XmlNode nodes in encoded.ChildNodes)
      {
        if( nodes.Name == "node" ) {
          _target_ni = new NodeInfo((XmlElement)nodes);
	}
	if( nodes.Name == "neighbors" ) {
          ArrayList neighs = new ArrayList();
          foreach(XmlNode neigh in nodes.ChildNodes) {
            if( neigh.Name == "node" ) {
              neighs.Add( new NodeInfo((XmlElement)neigh) );
	    }
	  }
	  _neighbors = new NodeInfo[ neighs.Count ];
	  neighs.CopyTo(_neighbors);
	}
      }
    }
    
    public ConnectToMessage(Direction dir, int id, XmlReader r)
    {
      if (r.Name.ToLower() != "connectto") {
        throw new ParseException("This is not a <connectTo /> message");
      }
      this.Dir = dir;
      this.Id = id;
      _ct = r["type"];
      _target_ni = null;
      
      bool reading_neigh = false;
      ArrayList neighbors = new ArrayList();
      while( r.Read() ) {
        if( r.NodeType == XmlNodeType.Element && r.Name.ToLower() == "node" ) {
          //This is the target node info:
	  NodeInfo tempni = new NodeInfo(r);
	  if( reading_neigh ) {
            neighbors.Add( tempni );
	  }
	  else {
	    _target_ni = tempni;
	  }
	}
	else if( r.Name.ToLower() == "neighbors" ) {
          if( r.NodeType == XmlNodeType.Element ) {
            //This is the start of the neighbors list
	    reading_neigh = true; 
	  }
	  else if( r.NodeType == XmlNodeType.EndElement ) {
            //This is the end
	    reading_neigh = false;
	    _neighbors = new NodeInfo[ neighbors.Count ];
	    neighbors.CopyTo(_neighbors);
	  }
	}
      }
    }

    protected string _ct;
    public string ConnectionType { get { return _ct; } }

    protected NodeInfo _target_ni;
    public NodeInfo Target {
      get { return _target_ni; }
    }
    protected NodeInfo[] _neighbors;
    public NodeInfo[] Neighbors { get { return _neighbors; } }

    public override bool CanReadTag(string tag)
    {
      return tag == "connectTo";
    }

    public override bool Equals(object o)
    {
      ConnectToMessage co = o as ConnectToMessage;
      if( co != null ) {
        bool same = true;
	same &= co.ConnectionType == _ct;
	same &= co.Target.Equals( _target_ni );
	if( _neighbors == null ) {
          same &= co.Neighbors == null;
	}
	else {
          int n_count = co.Neighbors.Length;
	  for(int i = 0; i < n_count; i++) {
            same &= co.Neighbors[i].Equals( Neighbors[i] );
	  } 
	}
	return same;
      }
      else {
        return false;
      }
    }
    
    public override IXmlAble ReadFrom(XmlElement el)
    {
      return new ConnectToMessage(el);
    }

    public override IXmlAble ReadFrom(XmlReader r)
    {
      Direction dir;
      int id;
      ReadStart(out dir, out id, r);
      return new ConnectToMessage(dir, id, r);
    }
    
    public override void WriteTo(XmlWriter w)
    {
      //Write the request or response and id
      base.WriteTo(w);  //<(request|response)>

      string ns = "";
      //Here we write out the specific stuff :
      w.WriteStartElement("connectTo", ns);     //<connectTo>
      //Write the attributes :
      w.WriteStartAttribute("type", ns);
      w.WriteString(_ct);
      w.WriteEndAttribute();
      //Write the NodeInfo
      _target_ni.WriteTo(w); 
      if( Neighbors != null ) {
        w.WriteStartElement("neighbors"); //<neighbors>
	foreach(NodeInfo n in Neighbors) {
          n.WriteTo(w);
	}
	w.WriteEndElement(); //</neighbors>
      }
      //end the connectTo element
      w.WriteEndElement();      //</connectTo>
      w.WriteEndElement();      //</(request|response)>
    }

  }
//Here are some Unit tests:
#if BRUNET_NUNIT
//Here are some NUnit 2 test fixtures
  [TestFixture]
  public class ConnectToMessageTester {

    public ConnectToMessageTester() { }

    [Test]
    public void CTMSerializationTest()
    {
      Address a = new DirectionalAddress(DirectionalAddress.Direction.Left);
      TransportAddress ta = new TransportAddress("brunet.tcp://127.0.0.1:5000"); 
      NodeInfo ni = new NodeInfo(a, ta);
      ConnectToMessage ctm1 = new ConnectToMessage(ConnectionType.Unstructured, ni);
      XmlAbleTester xt = new XmlAbleTester();
      
      ConnectToMessage ctm1a = (ConnectToMessage)xt.SerializeDeserialize(ctm1);
      Assert.AreEqual(ctm1, ctm1a, "CTM with 1 TA");

      //Test multiple tas:
      ArrayList tas = new ArrayList();
      tas.Add(ta);
      for(int i = 5001; i < 5010; i++)
        tas.Add(new TransportAddress("brunet.tcp://127.0.0.1:" + i.ToString()));
      NodeInfo ni2 = new NodeInfo(a, tas);

      ConnectToMessage ctm2 = new ConnectToMessage(ConnectionType.Structured, ni2);
      
      ConnectToMessage ctm2a = (ConnectToMessage)xt.SerializeDeserialize(ctm2);
      Assert.AreEqual(ctm2, ctm2a, "CTM with 10 TAs");
      //Here is a ConnectTo message with a neighbor list:
      NodeInfo[] neighs = new NodeInfo[5];
      for(int i = 0; i < 5; i++) {
        NodeInfo tmp =
		new NodeInfo(new DirectionalAddress(DirectionalAddress.Direction.Left),
	                     new TransportAddress("brunet.tcp://127.0.0.1:" + i.ToString())
			    );
	neighs[i] = tmp;
      }
      ConnectToMessage ctm3 = new ConnectToMessage("structured", ni, neighs);
      ConnectToMessage ctm3a = (ConnectToMessage)xt.SerializeDeserialize(ctm3);
      Assert.AreEqual(ctm2, ctm2a, "CTM with neighborlist");
#if false
      System.Console.WriteLine( ctm3.ToString() );
      foreach(NodeInfo tni in ctm3a.Neighbors) {
        System.Console.WriteLine(tni.ToString());
      }
#endif
    }
  }

#endif

}
