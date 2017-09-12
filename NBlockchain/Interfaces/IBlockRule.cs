﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NBlockchain.Models;

namespace NBlockchain.Interfaces
{
    public interface IBlockRule
    {
        bool Validate(Block block);
        bool TailRule { get; }
    }
}
