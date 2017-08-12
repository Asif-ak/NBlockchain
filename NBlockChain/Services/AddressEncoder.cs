﻿using NBlockChain.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using Wiry.Base32;

namespace NBlockChain.Services
{
    public class AddressEncoder : IAddressEncoder
    {
        public AddressEncoder()
        {
        }

        public string EncodeAddress(byte[] publicKey, byte type)
        {
            var result = new List<byte> {type};
            result.AddRange(publicKey);
            result.Add(CalculateCheckSum(result));

            return Base32Encoding.ZBase32.GetString(result.ToArray());
        }

        public byte[] ExtractPublicKey(string address)
        {
            var raw = Base32Encoding.ZBase32.ToBytes(address);
            return raw.Skip(1).Take(raw.Length - 2).ToArray();
        }

        public bool IsValidAddress(string address)
        {
            var raw = Base32Encoding.ZBase32.ToBytes(address);
            return (raw.Last() == CalculateCheckSum(raw.Take(raw.Length - 1)));
        }

        private byte CalculateCheckSum(IEnumerable<byte> data)
        {
            byte result = 0;

            foreach (var item in data)
            {
                result += item;
                result++;
            }

            return result;
        }
    }
}
