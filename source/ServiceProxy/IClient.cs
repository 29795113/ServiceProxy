﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceProxy
{
    public interface IClient
    {
        Task<ResponseData> Request(RequestData request);
    }
}
