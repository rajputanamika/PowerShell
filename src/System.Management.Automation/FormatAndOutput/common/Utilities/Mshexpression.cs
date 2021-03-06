// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// Class that represents the results from evaluating a PSPropertyExpression against an object.
    /// </summary>
    public class PSPropertyExpressionResult
    {

        /// <summary>
        /// Create a property expression result containing the original object, matching property expression
        /// and any exception generated during the match process.
        /// </summary>
        public PSPropertyExpressionResult(object res, PSPropertyExpression re, Exception e)
        {
            Result = res;
            ResolvedExpression = re;
            Exception = e;
        }

        /// <summary>
        /// The value of the object property matched by this property expression.
        /// </summary>
        public object Result { get; } = null;

        /// <summary>
        /// The original property expression fully resolved.
        /// </summary>
        public PSPropertyExpression ResolvedExpression { get; } = null;

        /// <summary>
        /// Any exception thrown while evaluating the expression.
        /// </summary>
        public Exception Exception { get; } = null;
    }

    /// <summary>
    /// PSPropertyExpression class. This class is used to get the names and/or values of properties
    /// on an object. A property expression can be constructed using either a wildcard expression string
    /// or a scriptblock to use to get the property value.
    /// </summary>
    public class PSPropertyExpression
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="s">Expression.</param>
        /// <exception cref="ArgumentNullException"></exception>
        public PSPropertyExpression(string s)
            : this(s, false)
        {
        }

        /// <summary>
        /// Create a property expression with a wildcard pattern.
        /// </summary>
        /// <param name="s">Property name pattern to match.</param>
        /// <param name="isResolved"><c>true</c> if no further attempts should be made to resolve wildcards.</param>
        /// <exception cref="ArgumentNullException"></exception>
        public PSPropertyExpression(string s, bool isResolved)
        {
            if (string.IsNullOrEmpty(s))
            {
                throw PSTraceSource.NewArgumentNullException("s");
            }

            _stringValue = s;
            _isResolved = isResolved;
        }

        /// <summary>
        /// Create a property expression with a ScriptBlock.
        /// </summary>
        /// <param name="scriptBlock">ScriptBlock to evaluate when retrieving the property value from an object.</param>
        /// <exception cref="ArgumentNullException"></exception>
        public PSPropertyExpression(ScriptBlock scriptBlock)
        {
            if (scriptBlock == null)
            {
                throw PSTraceSource.NewArgumentNullException("scriptBlock");
            }

            Script = scriptBlock;
        }

        /// <summary>
        /// The ScriptBlock for this expression to use when matching.
        /// </summary>
        public ScriptBlock Script { get; } = null;

        /// <summary>
        /// ToString() implementation for the property expression.
        /// </summary>
        public override string ToString()
        {
            if (Script != null)
                return Script.ToString();

            return _stringValue;
        }

        /// <summary>
        /// Resolve the names matched by this the expression.
        /// </summary>
        /// <param name="target">The object to apply the expression against.</param>
        public List<PSPropertyExpression> ResolveNames(PSObject target)
        {
            return ResolveNames(target, true);
        }

        /// <summary>
        /// Indicates if the pattern has wildcard characters in it. If the supplied pattern was
        /// a scriptblock, this will be false.
        /// </summary>
        public bool HasWildCardCharacters
        {
            get
            {
                if (Script != null)
                    return false;
                return WildcardPattern.ContainsWildcardCharacters(_stringValue);
            }
        }

        /// <summary>
        /// Resolve the names matched by this the expression.
        /// </summary>
        /// <param name="target">The object to apply the expression against.</param>
        /// <param name="expand">If the matched properties are property sets, expand them.</param>
        public List<PSPropertyExpression> ResolveNames(PSObject target, bool expand)
        {
            List<PSPropertyExpression> retVal = new List<PSPropertyExpression>();

            if (_isResolved)
            {
                retVal.Add(this);
                return retVal;
            }

            if (Script != null)
            {
                // script block, just add it to the list and be done
                PSPropertyExpression ex = new PSPropertyExpression(Script);

                ex._isResolved = true;
                retVal.Add(ex);
                return retVal;
            }

            // If the object passed in is a hashtable, then turn it into a PSCustomObject so
            // that property expressions can work on it.
            target = IfHashtableWrapAsPSCustomObject(target);

            // we have a string value
            IEnumerable<PSMemberInfo> members = null;
            if (HasWildCardCharacters)
            {
                // get the members first: this will expand the globbing on each parameter
                members = target.Members.Match(_stringValue,
                                            PSMemberTypes.Properties | PSMemberTypes.PropertySet | PSMemberTypes.Dynamic);
            }
            else
            {
                // we have no globbing: try an exact match, because this is quicker.
                PSMemberInfo x = target.Members[_stringValue];

                if ((x == null) && (target.BaseObject is System.Dynamic.IDynamicMetaObjectProvider))
                {
                    // We could check if GetDynamicMemberNames includes the name...  but
                    // GetDynamicMemberNames is only a hint, not a contract, so we'd want
                    // to attempt the binding whether it's in there or not.
                    x = new PSDynamicMember(_stringValue);
                }

                List<PSMemberInfo> temp = new List<PSMemberInfo>();
                if (x != null)
                {
                    temp.Add(x);
                }

                members = temp;
            }

            // we now have a list of members, we have to expand property sets
            // and remove duplicates
            List<PSMemberInfo> temporaryMemberList = new List<PSMemberInfo>();

            foreach (PSMemberInfo member in members)
            {
                // it can be a property set
                PSPropertySet propertySet = member as PSPropertySet;
                if (propertySet != null)
                {
                    if (expand)
                    {
                        // NOTE: we expand the property set under the
                        // assumption that it contains property names that
                        // do not require any further expansion
                        Collection<string> references = propertySet.ReferencedPropertyNames;

                        for (int j = 0; j < references.Count; j++)
                        {
                            ReadOnlyPSMemberInfoCollection<PSPropertyInfo> propertyMembers =
                                                target.Properties.Match(references[j]);
                            for (int jj = 0; jj < propertyMembers.Count; jj++)
                            {
                                temporaryMemberList.Add(propertyMembers[jj]);
                            }
                        }
                    }
                }
                // it can be a property
                else if (member is PSPropertyInfo)
                {
                    temporaryMemberList.Add(member);
                }
                // it can be a dynamic member
                else if (member is PSDynamicMember)
                {
                    temporaryMemberList.Add(member);
                }
            }

            Hashtable hash = new Hashtable();

            // build the list of unique values: remove the possible duplicates
            // from property set expansion
            foreach (PSMemberInfo m in temporaryMemberList)
            {
                if (!hash.ContainsKey(m.Name))
                {
                    PSPropertyExpression ex = new PSPropertyExpression(m.Name);

                    ex._isResolved = true;
                    retVal.Add(ex);
                    hash.Add(m.Name, null);
                }
            }

            return retVal;
        }

        /// <summary>
        /// Gets the values of the object properties matched by this expression.
        /// </summary>
        /// <param name="target">The object to match against.</param>
        public List<PSPropertyExpressionResult> GetValues(PSObject target)
        {
            return GetValues(target, true, true);
        }

        /// <summary>
        /// Gets the values of the object properties matched by this expression.
        /// </summary>
        /// <param name="target">The object to match against.</param>
        /// <param name="expand">If the matched properties are parameter sets, expand them.</param>
        /// <param name="eatExceptions">If true, any exceptions that occur during the match process are ignored.</param>
        public List<PSPropertyExpressionResult> GetValues(PSObject target, bool expand, bool eatExceptions)
        {
            List<PSPropertyExpressionResult> retVal = new List<PSPropertyExpressionResult>();

            // If the object passed in is a hashtable, then turn it into a PSCustomObject so
            // that property expressions can work on it.
            target = IfHashtableWrapAsPSCustomObject(target);

            // process the script case
            if (Script != null)
            {
                PSPropertyExpression scriptExpression = new PSPropertyExpression(Script);
                PSPropertyExpressionResult r = scriptExpression.GetValue(target, eatExceptions);
                retVal.Add(r);
                return retVal;
            }

            // process the expression
            List<PSPropertyExpression> resolvedExpressionList = this.ResolveNames(target, expand);

            foreach (PSPropertyExpression re in resolvedExpressionList)
            {
                PSPropertyExpressionResult r = re.GetValue(target, eatExceptions);
                retVal.Add(r);
            }

            return retVal;
        }

        #region Private Members

        private CallSite<Func<CallSite, object, object>> _getValueDynamicSite;

        private PSPropertyExpressionResult GetValue(PSObject target, bool eatExceptions)
        {
            try
            {
                object result = null;

                if (Script != null)
                {
                    result = Script.DoInvokeReturnAsIs(
                        useLocalScope: true,
                        errorHandlingBehavior: ScriptBlock.ErrorHandlingBehavior.WriteToExternalErrorPipe,
                        dollarUnder: target,
                        input: AutomationNull.Value,
                        scriptThis: AutomationNull.Value,
                        args: Utils.EmptyArray<object>());
                }
                else
                {
                    if (_getValueDynamicSite == null)
                    {
                        _getValueDynamicSite =
                            CallSite<Func<CallSite, object, object>>.Create(
                                    PSGetMemberBinder.Get(
                                        _stringValue,
                                        classScope: (Type) null,
                                        @static: false));
                    }

                    result = _getValueDynamicSite.Target.Invoke(_getValueDynamicSite, target);
                }

                return new PSPropertyExpressionResult(result, this, null);
            }
            catch (RuntimeException e)
            {
                if (eatExceptions)
                {
                    return new PSPropertyExpressionResult(null, this, e);
                }
                else
                {
                    throw;
                }
            }
        }

        private PSObject IfHashtableWrapAsPSCustomObject(PSObject target)
        {
            // If the object passed in is a hashtable, then turn it into a PSCustomObject so
            // that property expressions can work on it.
            if (PSObject.Base(target) is Hashtable targetAsHash)
            {
                target = (PSObject)(LanguagePrimitives.ConvertPSObjectToType(targetAsHash, typeof(PSObject), false, null, true));
            }

            return target;
        }

        // private members
        private string _stringValue;
        private bool _isResolved = false;

        #endregion Private Members
    }
}

