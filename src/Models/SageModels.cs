using System;
using System.Collections.Generic;

namespace DeliveryCloudApi.Models
{
    // Copy the existing Sage models from your original DeliveryService
    // These remain unchanged as Sage 300 structure doesn't change
    
    public class SageShipmentHeader
    {
        public decimal SHIUNIQ { get; set; }
        public string SHINUMBER { get; set; } = string.Empty;
        public string ORDNUMBER { get; set; } = string.Empty;
        public string CUSTOMER { get; set; } = string.Empty;
        public string BILNAME { get; set; } = string.Empty;
        public string SHPNAME { get; set; } = string.Empty;
        public string SHPADDR1 { get; set; } = string.Empty;
        public string SHPADDR2 { get; set; } = string.Empty;
        public string SHPADDR3 { get; set; } = string.Empty;
        public string SHPCITY { get; set; } = string.Empty;
        public string SHPSTATE { get; set; } = string.Empty;
        public string SHPZIP { get; set; } = string.Empty;
        public string SHPCOUNTRY { get; set; } = string.Empty;
        public string SHPCONTACT { get; set; } = string.Empty;
        public string SHPPHONE { get; set; } = string.Empty;
        public string SHPEMAIL { get; set; } = string.Empty;
        public string BILCONTACT { get; set; } = string.Empty;
        public string BILPHONE { get; set; } = string.Empty;
        public string BILEMAIL { get; set; } = string.Empty;
        public decimal SHIDATE { get; set; }
        public decimal AUDTDATE { get; set; }
        public decimal AUDTTIME { get; set; }
        public string ROUTE { get; set; } = string.Empty;
        public List<SageShipmentDetail> Details { get; set; } = new();
    }

    public class SageShipmentDetail
    {
        public decimal SHIUNIQ { get; set; }
        public short LINENUM { get; set; }
        public string ITEM { get; set; } = string.Empty;
        public string DESC { get; set; } = string.Empty;
        public decimal QTYSHIPPED { get; set; }
        public string SHIUNIT { get; set; } = string.Empty;
        public string ORDNUMBER { get; set; } = string.Empty;
        public short ORDDTLNUM { get; set; }
    }

    // Other existing Sage models...
}