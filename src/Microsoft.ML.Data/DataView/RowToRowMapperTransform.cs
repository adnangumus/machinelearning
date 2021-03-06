// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Model;

[assembly: LoadableClass(typeof(RowToRowMapperTransform), null, typeof(SignatureLoadDataTransform),
    "", RowToRowMapperTransform.LoaderSignature)]

namespace Microsoft.ML.Runtime.Data
{
    public sealed class RowMapperColumnInfo
    {
        public readonly string Name;
        public readonly ColumnType ColType;
        public readonly ColumnMetadataInfo Metadata;

        public RowMapperColumnInfo(string name, ColumnType type, ColumnMetadataInfo metadata)
        {
            Name = name;
            ColType = type;
            Metadata = metadata;
        }
    }

    /// <summary>
    /// This interface is used to create a <see cref="RowToRowMapperTransform"/>. 
    /// Implementations should be given an <see cref="ISchema"/> in their constructor, and should have a
    /// ctor or Create method with <see cref="SignatureLoadRowMapper"/>, along with a corresponding
    /// <see cref="LoadableClassAttribute"/>.
    /// </summary>
    public interface IRowMapper : ICanSaveModel
    {
        /// <summary>
        /// Returns the input columns needed for the requested output columns.
        /// </summary>
        Func<int, bool> GetDependencies(Func<int, bool> activeOutput);

        /// <summary>
        /// Returns the getters for the output columns given an active set of output columns. The length of the getters
        /// array should be equal to the number of columns added by the IRowMapper. It should contain the getter for the 
        /// i'th output column if activeOutput(i) is true, and null otherwise.
        /// </summary>
        Delegate[] CreateGetters(IRow input, Func<int, bool> activeOutput, out Action disposer);

        /// <summary>
        /// Returns information about the output columns, including their name, type and any metadata information.
        /// </summary>
        RowMapperColumnInfo[] GetOutputColumns();
    }

    public delegate void SignatureLoadRowMapper(ModelLoadContext ctx, ISchema schema);

    public abstract class MetadataInfo
    {
        public readonly ColumnType Type;

        protected MetadataInfo(ColumnType type)
        {
            Contracts.AssertValueOrNull(type);
            Type = type;
        }
    }

    public sealed class MetadataInfo<T> : MetadataInfo
    {
        public readonly MetadataUtils.MetadataGetter<T> Getter;

        public MetadataInfo(ColumnType type, MetadataUtils.MetadataGetter<T> getter)
            : base(type)
        {
            Getter = getter;
        }
    }

    public sealed class ColumnMetadataInfo
    {
        public readonly string Name;
        private readonly Dictionary<string, MetadataInfo> _infos;

        public ColumnMetadataInfo(string name)
        {
            Contracts.CheckNonWhiteSpace(name, nameof(name));

            Name = name;
            _infos = new Dictionary<string, MetadataInfo>();
        }

        public void Add(string kind, MetadataInfo info)
        {
            if (_infos.ContainsKey(kind))
                throw Contracts.Except("Already contains metadata of kind '{0}'", kind);
            _infos.Add(kind, info);
        }

        public Dictionary<string, MetadataInfo> Infos()
        {
            return _infos;
        }
    }

    /// <summary>
    /// This class is a transform that can add any number of output columns, that depend on any number of input columns.
    /// It does so with the help of an <see cref="IRowMapper"/>, that is given a schema in its constructor, and has methods
    /// to get the dependencies on input columns and the getters for the output columns, given an active set of output columns.
    /// </summary>
    public sealed class RowToRowMapperTransform : RowToRowTransformBase
    {
        private sealed class Bindings : ColumnBindingsBase
        {
            private readonly RowToRowMapperTransform _parent;
            public readonly RowMapperColumnInfo[] OutputColInfos;

            public Bindings(ISchema inputSchema, RowToRowMapperTransform parent)
                : base(inputSchema, true, Contracts.CheckRef(parent, nameof(parent))._mapper.GetOutputColumns().Select(info => info.Name).ToArray())
            {
                Contracts.AssertValue(parent);
                _parent = parent;
                OutputColInfos = _parent._mapper.GetOutputColumns().ToArray();
            }

            protected override ColumnType GetColumnTypeCore(int iinfo)
            {
                Contracts.Assert(0 <= iinfo & iinfo < OutputColInfos.Length);
                return OutputColInfos[iinfo].ColType;
            }

            /// <summary>
            /// Produces the set of active columns for the data view (as a bool[] of length bindings.ColumnCount),
            /// a predicate for the needed active input columns, and a predicate for the needed active
            /// output columns.
            /// </summary>
            public bool[] GetActive(Func<int, bool> predicate, out Func<int, bool> predicateInput)
            {
                var active = GetActive(predicate);
                Contracts.Assert(active.Length == ColumnCount);

                var activeInput = GetActiveInput(predicate);
                Contracts.Assert(activeInput.Length == Input.ColumnCount);

                // Get a predicate that determines which outputs are active.
                var predicateOut = GetActiveOutputColumns(active);

                // Now map those to active input columns.
                var predicateIn = _parent._mapper.GetDependencies(predicateOut);

                // Combine the two sets of input columns.
                predicateInput =
                    col => 0 <= col && col < activeInput.Length && (activeInput[col] || predicateIn(col));

                return active;
            }

            public Func<int, bool> GetActiveOutputColumns(bool[] active)
            {
                Contracts.AssertValue(active);
                Contracts.Assert(active.Length == ColumnCount);

                return
                    col =>
                    {
                        Contracts.Assert(0 <= col && col < OutputColInfos.Length);
                        return 0 <= col && col < OutputColInfos.Length && active[MapIinfoToCol(col)];
                    };
            }

            protected override IEnumerable<KeyValuePair<string, ColumnType>> GetMetadataTypesCore(int iinfo)
            {
                return _parent._md.GetMetadataTypes(iinfo);
            }

            protected override ColumnType GetMetadataTypeCore(string kind, int iinfo)
            {
                return _parent._md.GetMetadataTypeOrNull(kind, iinfo);
            }

            protected override void GetMetadataCore<TValue>(string kind, int iinfo, ref TValue value)
            {
                _parent._md.GetMetadata(_parent.Host, kind, iinfo, ref value);
            }

            public bool TryGetInfoIndex(string name, out int iinfo)
            {
                return TryGetColumnIndexCore(name, out iinfo);
            }
        }

        private readonly IRowMapper _mapper;
        private readonly Bindings _bindings;
        private readonly MetadataDispatcher _md;

        public const string RegistrationName = "RowToRowMapperTransform";
        public const string LoaderSignature = "RowToRowMapper";
        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "ROW MPPR",
                verWrittenCur: 0x00010001, // Initial
                verReadableCur: 0x00010001,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature);
        }

        public override ISchema Schema { get { return _bindings; } }

        public RowToRowMapperTransform(IHostEnvironment env, IDataView input, IRowMapper mapper)
            : base(env, RegistrationName, input)
        {
            Contracts.CheckValue(mapper, nameof(mapper));
            _mapper = mapper;
            _bindings = new Bindings(input.Schema, this);
            CreateMetadata(_bindings.OutputColInfos.Select(info => info.Metadata), _bindings, out _md);
        }

        private RowToRowMapperTransform(IHost host, ModelLoadContext ctx, IDataView input)
            : base(host, input)
        {
            // *** Binary format ***
            // _mapper

            ctx.LoadModel<IRowMapper, SignatureLoadRowMapper>(host, out _mapper, "Mapper", input.Schema);
            _bindings = new Bindings(input.Schema, this);
            CreateMetadata(_mapper.GetOutputColumns().Select(info => info.Metadata), _bindings, out _md);
        }

        public static RowToRowMapperTransform Create(IHostEnvironment env, ModelLoadContext ctx, IDataView input)
        {
            Contracts.CheckValue(env, nameof(env));
            var h = env.Register(RegistrationName);
            h.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());
            h.CheckValue(input, nameof(input));
            return h.Apply("Loading Model", ch => new RowToRowMapperTransform(h, ctx, input));
        }

        public override void Save(ModelSaveContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // _mapper

            ctx.SaveModel(_mapper, "Mapper");
        }

        private static void CreateMetadata(IEnumerable<ColumnMetadataInfo> infos, Bindings bindings, out MetadataDispatcher md)
        {
            Contracts.AssertValue(bindings);

            md = new MetadataDispatcher(bindings.InfoCount);
            foreach (var colInfo in infos)
            {
                if (colInfo == null)
                    continue;
                int iinfo;
                var found = bindings.TryGetInfoIndex(colInfo.Name, out iinfo);
                Contracts.Assert(found);
                using (var bldr = md.BuildMetadata(iinfo))
                {
                    foreach (var info in colInfo.Infos())
                    {
                        Action<MetadataDispatcher.Builder, string, MetadataInfo<int>> del = AddGetter;
                        var meth =
                            del.GetMethodInfo().GetGenericMethodDefinition().MakeGenericMethod(info.Value.Type.RawType);
                        meth.Invoke(null, new object[] { bldr, info.Key, info.Value });
                    }
                }
            }
            md.Seal();
        }

        private static void AddGetter<T>(MetadataDispatcher.Builder bldr, string kind, MetadataInfo<T> info)
        {
            bldr.AddGetter(kind, info.Type, info.Getter);
        }

        protected override bool? ShouldUseParallelCursors(Func<int, bool> predicate)
        {
            Host.AssertValue(predicate, "predicate");
            if (_bindings.AnyNewColumnsActive(predicate))
                return true;
            return null;
        }

        protected override IRowCursor GetRowCursorCore(Func<int, bool> predicate, IRandom rand = null)
        {
            Func<int, bool> predicateInput;
            var active = _bindings.GetActive(predicate, out predicateInput);
            return new RowCursor(Host, Source.GetRowCursor(predicateInput, rand), this, active);
        }

        public override IRowCursor[] GetRowCursorSet(out IRowCursorConsolidator consolidator, Func<int, bool> predicate, int n, IRandom rand = null)
        {
            Host.CheckValue(predicate, nameof(predicate));
            Host.CheckValueOrNull(rand);

            Func<int, bool> predicateInput;
            var active = _bindings.GetActive(predicate, out predicateInput);

            var inputs = Source.GetRowCursorSet(out consolidator, predicateInput, n, rand);
            Host.AssertNonEmpty(inputs);

            if (inputs.Length == 1 && n > 1 && _bindings.AnyNewColumnsActive(predicate))
                inputs = DataViewUtils.CreateSplitCursors(out consolidator, Host, inputs[0], n);
            Host.AssertNonEmpty(inputs);

            var cursors = new IRowCursor[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
                cursors[i] = new RowCursor(Host, inputs[i], this, active);
            return cursors;
        }

        private sealed class RowCursor : SynchronizedCursorBase<IRowCursor>, IRowCursor
        {
            private readonly Delegate[] _getters;
            private readonly bool[] _active;
            private readonly Bindings _bindings;
            private readonly Action _disposer;

            public ISchema Schema { get { return _bindings; } }

            public RowCursor(IChannelProvider provider, IRowCursor input, RowToRowMapperTransform parent, bool[] active)
                : base(provider, input)
            {
                var pred = parent._bindings.GetActiveOutputColumns(active);
                _getters = parent._mapper.CreateGetters(input, pred, out _disposer);
                _active = active;
                _bindings = parent._bindings;
            }

            public bool IsColumnActive(int col)
            {
                Ch.Check(0 <= col && col < _bindings.ColumnCount);
                return _active[col];
            }

            public ValueGetter<TValue> GetGetter<TValue>(int col)
            {
                Ch.Check(IsColumnActive(col));

                bool isSrc;
                int index = _bindings.MapColumnIndex(out isSrc, col);
                if (isSrc)
                    return Input.GetGetter<TValue>(index);

                Ch.AssertValue(_getters);
                var getter = _getters[index];
                Ch.Assert(getter != null);
                var fn = getter as ValueGetter<TValue>;
                if (fn == null)
                    throw Ch.Except("Invalid TValue in GetGetter: '{0}'", typeof(TValue));
                return fn;
            }

            public override void Dispose()
            {
                _disposer?.Invoke();
                base.Dispose();
            }
        }
    }
}
