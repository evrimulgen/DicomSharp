using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using DicomSharp.Data;
using DicomSharp.Dictionary;
using Microsoft.Practices.Unity;
using log4net;
using log4net.Config;
using Timer = System.Timers.Timer;

[assembly: XmlConfigurator(Watch = true)]

namespace DicomSharp.Net
{
    /// <summary>
    /// Summary description for ServiceClassUser.
    /// </summary>
    public class ServiceClassUser : IServiceClassUser
    {
        private const string TransferSyntaxUniqueId = UIDs.ImplicitVRLittleEndian;
        private const int ASSOCIATE_TIME_OUT = 0;
        private const string SOP_CLASS_UNIQUEID_NOT_SUPPORTED = "SOP class UniqueId not supported";
        private static readonly ILog Logger = LogManager.GetLogger(typeof(ServiceClassUser));
        private static readonly String[] DefinedTransferSyntaxes = new[] { TransferSyntaxUniqueId };
        private readonly AAssociateRQ _aAssociateRequest;
        private readonly AssociationFactory _associationFactory;
        private readonly Dictionary<string, IList<DataSet>> _cacheManager = new Dictionary<string, IList<DataSet>>(50);
        private readonly IUnityContainer _container;
        private readonly DcmObjectFactory _dcmObjectFactory;
        private readonly DcmParserFactory _dcmParserFactory;
        private IActiveAssociation _activeAssociation;
        private string _hostName = "localhost";
        private int _port = 104;
        private int _presentationContextIdStart = 1;

        public ServiceClassUser(IUnityContainer unityContainer, String name, String title, String hostName, int port)
        {
            _container = unityContainer;
            _associationFactory = _container.Resolve<AssociationFactory>();
            _dcmObjectFactory = _container.Resolve<DcmObjectFactory>();
            _dcmParserFactory = _container.Resolve<DcmParserFactory>();
            _aAssociateRequest = _associationFactory.NewAAssociateRQ();
            SetUpForOperation(name, title, hostName, port);
            var timer = new Timer(10 * 60 * 1000);
            timer.Elapsed += ClearCache;
            timer.Start();
        }

        #region IServiceClassUser Members

        public string Name
        {
            get { return _aAssociateRequest.Name; }
            set { _aAssociateRequest.Name = value; }
        }

        public string HostName
        {
            get { return _hostName; }
            set { _hostName = value; }
        }

        public string Title
        {
            get { return _aAssociateRequest.ApplicationEntityTitle; }
            set { _aAssociateRequest.ApplicationEntityTitle = value; }
        }

        public uint Port
        {
            get { return (uint)_port; }
            set { _port = (int)value; }
        }

        /// <summary>
        /// This method configures the Service Class User to operate against a specific Service Class Provider
        /// <param name="name">The name of the SCU that will be sent to the SCP</param>
        /// <param name="title">The AE Title of the SCP</param>
        /// <param name="newHostName">The hostname of the SCP</param>
        /// <param name="newPort">The newPort of the SCP</param>
        /// </summary>
        public void SetUpForOperation(string name, string title, string newHostName, int newPort)
        {
            _aAssociateRequest.ApplicationEntityTitle = title;
            _aAssociateRequest.Name = name;
            _aAssociateRequest.AsyncOpsWindow = _associationFactory.NewAsyncOpsWindow(0, 1);
            _aAssociateRequest.MaxPduLength = 16352;
            _hostName = newHostName;
            _port = newPort;
            _presentationContextIdStart = 1;
        }

        /// <summary>
        /// Send C-ECHO, <see cref="SetUpForOperation"/> to specify the endpoint for the echo
        /// </summary>
        public bool CEcho()
        {
            int pcid = _presentationContextIdStart;
            _presentationContextIdStart += 2;
            bool success = false;
            IActiveAssociation active = null;
            try
            {
                _aAssociateRequest.AddPresContext(_associationFactory.NewPresContext(pcid, UIDs.Verification, DefinedTransferSyntaxes));
                active = OpenAssociation();
                if (active != null)
                {
                    if (active.Association.GetAcceptedTransferSyntaxUID(pcid) == null)
                    {
                        Logger.Error("Verification SOP class is not supported");
                    }
                    else
                    {
                        IDicomCommand cEchoDicomCommand = _dcmObjectFactory.NewCommand().InitCEchoRQ(0);
                        IDimse dimse = _associationFactory.NewDimse(pcid, cEchoDicomCommand);
                        Console.Out.WriteLine("{0} {1}", Logger.Logger.Name, Logger.Logger.Repository);
                        Logger.Info(String.Format(" {0} Echoing @ {1} on {2}:{3}", _aAssociateRequest.Name, _aAssociateRequest.ApplicationEntityTitle, _hostName, _port));
                        active.Invoke(dimse);
                        success = true;
                    }
                }
            }
            finally
            {
                if (active != null)
                {
                    active.Release(true);
                }
                _aAssociateRequest.RemovePresentationContext(pcid);
            }
            return success;
        }

        public bool Cancel()
        {
            int pcid = _presentationContextIdStart;
            _presentationContextIdStart += 2;
            bool success;
            IActiveAssociation cancelAssociation = OpenAssociation();
            try
            {
                IDicomCommand cCancelRequest = _dcmObjectFactory.NewCommand().InitCCancelRQ(_activeAssociation.Association.CurrentMessageId());
                IDimse dimse = _associationFactory.NewDimse(pcid, cCancelRequest);
                IActiveAssociation active = _associationFactory.NewActiveAssociation(cancelAssociation.Association, null);
                active.Start();
                active.Invoke(dimse);
                success = true;
            }
            finally
            {
                _activeAssociation.Release(true);
                cancelAssociation.Release(true);
                _aAssociateRequest.RemovePresentationContext(pcid - 2);
                _aAssociateRequest.RemovePresentationContext(pcid);
            }
            return success;
        }

        /// <summary>
        /// Send C-FIND for series of a study
        /// <param name="studyInstanceUniqueId">A study instance UniqueId</param>
        /// </summary>
        public IList<DataSet> CFindSeriesForStudy(string studyInstanceUniqueId)
        {
            if (_cacheManager.ContainsKey(studyInstanceUniqueId))
            {
                return _cacheManager[studyInstanceUniqueId];
            }
            IList<DataSet> datasets = CFindSeriesForStudies(new List<string> { studyInstanceUniqueId });
            if (datasets.Any())
            {
                _cacheManager.Add(studyInstanceUniqueId, datasets);
            }
            return datasets;
        }

        /// <summary>
        /// Send C-FIND to find the series for the studies 
        /// <param name="studiesInstanceUniqueIds">The studies' instance UniqueIds</param>
        /// </summary>
        public IList<DataSet> CFindSeriesForStudies(IEnumerable<string> studiesInstanceUniqueIds)
        {
            var dataset = new DataSet();
            const string sopClassUniqueId = UIDs.StudyRootQueryRetrieveInformationModelFIND;
            dataset.FileMetaInfo = GenerateFileMetaInfo(sopClassUniqueId);
            dataset.PutCS(Tags.QueryRetrieveLevel, "SERIES");
            dataset.PutCS(Tags.Modality);
            dataset.PutUI(Tags.StudyInstanceUniqueId, studiesInstanceUniqueIds.ToArray());
            dataset.PutUI(Tags.SeriesInstanceUniqueId);
            dataset.PutIS(Tags.SeriesNumber);
            dataset.PutDA(Tags.SeriesDate);
            dataset.PutTM(Tags.SeriesTime);
            dataset.PutLO(Tags.SeriesDescription);
            return studiesInstanceUniqueIds != null && studiesInstanceUniqueIds.Any() ? RetrieveDatasetsFromServiceClassProvider(dataset, sopClassUniqueId) : new List<DataSet>();
        }

        /// <summary>
        /// Send C-FIND for series
        /// <param name="seriesInstanceUniqueId">A series instance UniqueId</param>
        /// </summary>
        public IList<DataSet> CFindSeries(string seriesInstanceUniqueId)
        {
            if (_cacheManager.ContainsKey(seriesInstanceUniqueId))
            {
                return _cacheManager[seriesInstanceUniqueId];
            }
            IList<DataSet> datasets = CFindSeriesForStudies(new List<string> { seriesInstanceUniqueId });
            if (datasets.Any())
            {
                _cacheManager.Add(seriesInstanceUniqueId, datasets);
            }
            return datasets;
        }

        /// <summary>
        /// Send C-FIND for series
        /// <param name="seriesInstanceUniqueIds">The series' instance UniqueIds</param>
        /// </summary>
        public IList<DataSet> CFindSeries(IEnumerable<string> seriesInstanceUniqueIds)
        {
            var dataset = new DataSet();
            const string sopClassUniqueId = UIDs.StudyRootQueryRetrieveInformationModelFIND;
            dataset.FileMetaInfo = GenerateFileMetaInfo(sopClassUniqueId);
            dataset.PutCS(Tags.QueryRetrieveLevel, "SERIES");
            dataset.PutCS(Tags.Modality);
            dataset.PutUI(Tags.SeriesInstanceUniqueId, seriesInstanceUniqueIds.ToArray());
            dataset.PutUI(Tags.StudyInstanceUniqueId);
            dataset.PutIS(Tags.SeriesNumber);
            dataset.PutDA(Tags.SeriesDate);
            dataset.PutTM(Tags.SeriesTime);
            dataset.PutLO(Tags.SeriesDescription);
            return seriesInstanceUniqueIds != null && seriesInstanceUniqueIds.Any() ? RetrieveDatasetsFromServiceClassProvider(dataset, sopClassUniqueId) : new List<DataSet>();
        }

        /// <summary>
        /// Find all the studies for a specific patient
        /// <param name="patientId">The id of the patient</param>
        /// <param name="patientName">The name of the patient</param>
        /// </summary>
        public IList<DataSet> CFindStudy(string patientId, string patientName)
        {
            string queryKey = _aAssociateRequest.ApplicationEntityTitle + _port + _hostName + patientId + patientName;
            if (_cacheManager.ContainsKey(queryKey))
            {
                return _cacheManager[queryKey];
            }
            const string sopClassUniqueId = UIDs.StudyRootQueryRetrieveInformationModelFIND;
            var dataset = new DataSet();
            dataset.FileMetaInfo = GenerateFileMetaInfo(sopClassUniqueId);
            dataset.PutDA(Tags.StudyDate);
            dataset.PutTM(Tags.StudyTime);
            dataset.PutSH(Tags.AccessionNumber);
            dataset.PutCS(Tags.QueryRetrieveLevel, "STUDY");
            dataset.PutCS(Tags.ModalitiesInStudy);
            dataset.PutLO(Tags.InstitutionName);
            dataset.PutPN(Tags.PerformingPhysicianName);
            dataset.PutPN(Tags.ReferringPhysicianName);
            dataset.PutLO(Tags.StudyDescription);
            dataset.PutPN(Tags.PatientName, patientName);
            dataset.PutLO(Tags.PatientID, patientId);
            dataset.PutDA(Tags.PatientBirthDate);
            dataset.PutCS(Tags.PatientSex);
            dataset.PutAS(Tags.PatientAge);
            dataset.PutUI(Tags.StudyInstanceUniqueId);
            dataset.PutSH(Tags.StudyID);
            List<DataSet> datasets = RetrieveDatasetsFromServiceClassProvider(dataset, sopClassUniqueId);
            if (datasets.Any())
            {
                _cacheManager.Add(queryKey, datasets);
            }
            return datasets;
        }

        /// <summary>
        /// Find a the study for the study Instance UniqueIds
        /// <param name="studyInstanceUniqueId">The studu instance UniqueIds</param>
        /// </summary>
        public IList<DataSet> CFindStudy(string studyInstanceUniqueId)
        {
            if (_cacheManager.ContainsKey(studyInstanceUniqueId))
            {
                return _cacheManager[studyInstanceUniqueId];
            }
            IList<DataSet> datasets = CFindStudies(new List<string> { studyInstanceUniqueId });
            if (datasets.Count > 0)
            {
                _cacheManager.Add(studyInstanceUniqueId, datasets);
            }
            return datasets;
        }

        /// <summary>
        /// Find all the studies for the studies Instance UniqueIds
        /// <param name="studyInstanceUniqueIds">The studies' instance UniqueIds</param>
        /// </summary>
        public IList<DataSet> CFindStudies(IEnumerable<string> studyInstanceUniqueIds)
        {
            const string sopClassUniqueId = UIDs.StudyRootQueryRetrieveInformationModelFIND;
            var dataset = new DataSet();
            dataset.FileMetaInfo = GenerateFileMetaInfo(sopClassUniqueId);
            dataset.PutDA(Tags.StudyDate);
            dataset.PutTM(Tags.StudyTime);
            dataset.PutSH(Tags.AccessionNumber);
            dataset.PutCS(Tags.QueryRetrieveLevel, "STUDY");
            dataset.PutCS(Tags.ModalitiesInStudy);
            dataset.PutLO(Tags.InstitutionName);
            dataset.PutPN(Tags.ReferringPhysicianName);
            dataset.PutLO(Tags.StudyDescription);
            dataset.PutPN(Tags.PatientName);
            dataset.PutLO(Tags.PatientID);
            dataset.PutDA(Tags.PatientBirthDate);
            dataset.PutCS(Tags.PatientSex);
            dataset.PutAS(Tags.PatientAge);
            dataset.PutUI(Tags.StudyInstanceUniqueId, studyInstanceUniqueIds.ToArray());
            dataset.PutSH(Tags.StudyID);
            return studyInstanceUniqueIds.Any() ? RetrieveDatasetsFromServiceClassProvider(dataset, sopClassUniqueId) : new List<DataSet>();
        }

        /// <summary>
        /// Send C-FIND for instance
        /// <param name="studyInstanceUniqueIds">The studies' instance UniqueIds</param>
        /// <param name="seriesInstanceUniqueIds">The series' instance UniqueIds</param>
        /// </summary>
        public IList<DataSet> CFindInstance(IEnumerable<string> studyInstanceUniqueIds, IEnumerable<string> seriesInstanceUniqueIds)
        {
            const string sopClassUniqueId = UIDs.StudyRootQueryRetrieveInformationModelFIND;
            var datasets = new List<DataSet>();
            List<string> seriesNotCached = RetrieveItemsFromTheCache(seriesInstanceUniqueIds, datasets);
            List<string> studiesNotCached = RetrieveItemsFromTheCache(studyInstanceUniqueIds, datasets);
            var dataset = new DataSet();
            dataset.FileMetaInfo = GenerateFileMetaInfo(sopClassUniqueId);
            dataset.PutUI(Tags.SOPInstanceUniqueId);
            dataset.PutCS(Tags.QueryRetrieveLevel, "IMAGE");
            dataset.PutUI(Tags.SeriesInstanceUniqueId, seriesNotCached.ToArray());
            dataset.PutUI(Tags.StudyInstanceUniqueId, studiesNotCached.ToArray());
            datasets.AddRange(RetrieveDatasetsFromServiceClassProvider(dataset, sopClassUniqueId));
            return datasets;
        }

        /// <summary>
        /// Send C-Move for studies and series to be stored in the specified applicationEntityDestination
        /// <param name="studyInstanceUniqueIds">The studies' instance UniqueIds</param>
        /// <param name="seriesInstanceUniqueIds">The series' instance UniqueIds</param>
        /// <param name="applicationEntityDestination">The SCP that will store the files</param>
        /// </summary>
        public IList<DataSet> CMove(IEnumerable<string> studyInstanceUniqueIds, IEnumerable<string> seriesInstanceUniqueIds, string applicationEntityDestination)
        {
            IList<DataSet> studyDataSets = CFindSeriesForStudies(studyInstanceUniqueIds);
            IList<DataSet> seriesDataSets = CFindSeries(seriesInstanceUniqueIds);
            foreach (DataSet studyDataSet in studyDataSets)
            {
                seriesDataSets.Add(studyDataSet);
            }
            MoveSeries(seriesDataSets, applicationEntityDestination);
            return seriesDataSets;
        }

        private void MoveSeries(IEnumerable<DataSet> seriesDataSets, string applicationEntityDestination)
        {
            IEnumerable<IGrouping<string, DataSet>> groupedSeries = from series in seriesDataSets
                                                                    from dataSet in series.GetElements()
                                                                    where dataSet.Tag == Tags.StudyInstanceUniqueId
                                                                    group series by dataSet.GetString(Encoding.ASCII);
            foreach (var grouping in groupedSeries)
            {
                MoveSeriesSelectedWithinStudy(applicationEntityDestination, grouping);
            }
        }

        private void MoveSeriesSelectedWithinStudy(string applicationEntityDestination, IGrouping<string, DataSet> study)
        {
            IEnumerable<string> seriesInstanceUniqueIds = from dataSet in study from element in dataSet.GetElements() where element.Tag == Tags.SeriesInstanceUniqueId select element.GetString(Encoding.ASCII);
            MoveSeries(study.Key, seriesInstanceUniqueIds, applicationEntityDestination);
        }

        /// <summary>
        /// Send C-GET
        /// <param name="studyInstanceUniqueId">A study instance UniqueId</param>
        /// <param name="seriesInstanceUniqueId">A series instance UniqueId</param>
        /// <param name="sopInstanceUniqueId">A sop instance instance UniqueId</param>
        /// </summary>
        public IList<DataSet> CGet(string studyInstanceUniqueId, string seriesInstanceUniqueId, string sopInstanceUniqueId)
        {
            if ((studyInstanceUniqueId == null) && (seriesInstanceUniqueId == null) && (sopInstanceUniqueId == null))
            {
                return null;
            }
            int pcid = _presentationContextIdStart;
            _presentationContextIdStart += 2;
            var datasets = new List<DataSet>();
            IActiveAssociation active = null;
            try
            {
                string sopClassUniqueId = UIDs.StudyRootQueryRetrieveInformationModelGET;
                _aAssociateRequest.AddPresContext(_associationFactory.NewPresContext(pcid, sopClassUniqueId, DefinedTransferSyntaxes));
                active = OpenAssociation();
                if (active != null)
                {
                    var dataset = new DataSet();
                    dataset.FileMetaInfo = GenerateFileMetaInfo(sopClassUniqueId);
                    dataset.PutUI(Tags.StudyInstanceUniqueId, studyInstanceUniqueId);
                    dataset.PutUI(Tags.SeriesInstanceUniqueId, seriesInstanceUniqueId);
                    dataset.PutUI(Tags.SOPInstanceUniqueId, sopInstanceUniqueId);
                    IAssociation association = active.Association;
                    if ((association.GetAcceptedPresContext(sopClassUniqueId, TransferSyntaxUniqueId)) == null)
                    {
                        Logger.Error(SOP_CLASS_UNIQUEID_NOT_SUPPORTED);
                        return null;
                    }
                    DicomCommand cGetDicomCommand = _dcmObjectFactory.NewCommand().InitCGetRQ(association.NextMsgID(), sopClassUniqueId, Priority.HIGH);
                    IDimse dimseRequest = _associationFactory.NewDimse(pcid, cGetDicomCommand, dataset);
                    FutureDimseResponse dimseResponse = active.Invoke(dimseRequest);
                    while (!dimseResponse.IsReady())
                    {
                        Thread.Sleep(0);
                    }
                    datasets.AddRange(dimseResponse.ListPending().Select(dimse => dimse.DataSet));
                    _aAssociateRequest.RemovePresentationContext(pcid);
                }
            }
            finally
            {
                if (active != null)
                {
                    active.Release(true);
                }
                _aAssociateRequest.RemovePresentationContext(pcid);
            }
            return datasets;
        }

        #endregion

        private static void LogDataSetValues(DataSet seriesDataSets)
        {
            if (seriesDataSets == null)
            {
                return;
            }
            Logger.Info(seriesDataSets.GetElementsAsString());
        }

        private void MoveSeries(string studyInstanceUniqueId, IEnumerable<string> seriesInstanceUniqueIds, string applicationEntityDestination)
        {
            DataSet seriesCMoveDataset = new DataSet();
            seriesCMoveDataset.FileMetaInfo = GenerateFileMetaInfo(UIDs.StudyRootQueryRetrieveInformationModelMOVE);
            seriesCMoveDataset.PutCS(Tags.QueryRetrieveLevel, "SERIES");
            seriesCMoveDataset.PutUI(Tags.StudyInstanceUniqueId, studyInstanceUniqueId);
            seriesCMoveDataset.PutUI(Tags.SeriesInstanceUniqueId, seriesInstanceUniqueIds.Distinct().ToArray());
            LogDataSetValues(seriesCMoveDataset);
            CMoveDataSet(seriesCMoveDataset, applicationEntityDestination);
        }

        private void CMoveDataSet(DataSet dataset, string applicationEntityDestination)
        {
            int pcid = _presentationContextIdStart;
            _presentationContextIdStart += 2;
            IActiveAssociation activeAssociation = null;
            try
            {
                const string sopClassUniqueId = UIDs.StudyRootQueryRetrieveInformationModelMOVE;
                _aAssociateRequest.AddPresContext(_associationFactory.NewPresContext(pcid, sopClassUniqueId, DefinedTransferSyntaxes));
                activeAssociation = OpenAssociation();
                if (activeAssociation != null)
                {
                    IAssociation association = activeAssociation.Association;
                    if (association.GetAcceptedPresContext(sopClassUniqueId, TransferSyntaxUniqueId) == null)
                    {
                        Logger.Error(SOP_CLASS_UNIQUEID_NOT_SUPPORTED);
                    }
                    else
                    {
                        string message = String.Format("CMove from {0} @ {1} {2}:{3} to {4}", _aAssociateRequest.Name, _aAssociateRequest.ApplicationEntityTitle, _hostName, _port, applicationEntityDestination);
                        Logger.Info(message);
                        IDicomCommand dicomCommand = _dcmObjectFactory.NewCommand();
                        IDicomCommand cMoveRequest = dicomCommand.InitCMoveRQ(association.NextMsgID(), sopClassUniqueId, Priority.HIGH, applicationEntityDestination);
                        IDimse dimseRequest = _associationFactory.NewDimse(pcid, cMoveRequest, dataset);
                        FutureDimseResponse dimseResponse = activeAssociation.Invoke(dimseRequest);
                        while (!dimseResponse.IsReady())
                        {
                            Thread.Sleep(0);
                        }
                        Logger.Info("Finished CMOVE");
                    }
                }
            }
            finally
            {
                if (activeAssociation != null)
                {
                    activeAssociation.Release(false);
                }
                _aAssociateRequest.RemovePresentationContext(pcid);
            }
        }

        /// <summary>
        /// Send C-STORE
        /// </summary>
        /// <param name="fileName"></param>
        public bool CStore(String fileName)
        {
            int pcid = _presentationContextIdStart;
            _presentationContextIdStart += 2;

            Stream ins = null;
            DcmParser dcmParser = null;
            DataSet dataSet = null;
            IActiveAssociation active = null;
            try
            {
                // Load DICOM file
                try
                {
                    ins = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read));
                    dcmParser = _dcmParserFactory.NewDcmParser(ins);
                    FileFormat format = dcmParser.DetectFileFormat();
                    if (format != null)
                    {
                        dataSet = _dcmObjectFactory.NewDataset();
                        dcmParser.DcmHandler = dataSet.DcmHandler;
                        dcmParser.ParseDcmFile(format, Tags.PixelData);
                        Logger.Debug("Reading done");
                    }
                    else
                    {
                        Logger.Error("Unknown format!");
                    }
                }
                catch (IOException e)
                {
                    Logger.Error(e);
                }

                //
                // Prepare association
                //
                string classUniqueId = dataSet.GetString(Tags.SOPClassUniqueId);
                string tsUniqueId = dataSet.GetString(Tags.TransferSyntaxUniqueId);

                if ((tsUniqueId == null || tsUniqueId.Equals("")) && (dataSet.FileMetaInfo != null))
                {
                    tsUniqueId = dataSet.FileMetaInfo.GetString(Tags.TransferSyntaxUniqueId);
                }

                if (tsUniqueId == null || tsUniqueId.Equals(""))
                {
                    tsUniqueId = UIDs.ImplicitVRLittleEndian;
                }

                _aAssociateRequest.AddPresContext(_associationFactory.NewPresContext(pcid, classUniqueId, new[] { tsUniqueId }));
                active = OpenAssociation();
                if (active != null)
                {
                    bool bResponse = false;

                    FutureDimseResponse frsp = SendDataset(active, dcmParser, dataSet);
                    if (frsp != null)
                    {
                        active.WaitOnResponse();
                        bResponse = true;
                    }
                    active.Release(true);
                    return bResponse;
                }
            }
            finally
            {
                if (active != null)
                {
                    active.Release(true);
                }
                _aAssociateRequest.RemovePresentationContext(pcid);
                if (ins != null)
                {
                    try
                    {
                        ins.Close();
                    }
                    catch (IOException) { }
                }
            }

            return false;
        }

        /// <summary>
        /// Send C-STORE
        /// </summary>
        /// <param name="dataSet"></param>
        public bool CStore(DataSet dataSet)
        {
            int pcid = _presentationContextIdStart;
            _presentationContextIdStart += 2;
            IActiveAssociation active = null;
            try
            {
                //
                // Prepare association
                //
                String classUniqueId = dataSet.GetString(Tags.SOPClassUniqueId);
                String tsUniqueId = dataSet.GetString(Tags.TransferSyntaxUniqueId);

                if (string.IsNullOrEmpty(tsUniqueId) && (dataSet.FileMetaInfo != null))
                {
                    tsUniqueId = dataSet.FileMetaInfo.GetString(Tags.TransferSyntaxUniqueId);
                }

                if (string.IsNullOrEmpty(tsUniqueId))
                {
                    tsUniqueId = UIDs.ImplicitVRLittleEndian;
                }

                _aAssociateRequest.AddPresContext(_associationFactory.NewPresContext(pcid, classUniqueId, new[] { tsUniqueId }));
                active = OpenAssociation();
                if (active != null)
                {
                    bool bResponse = false;
                    FutureDimseResponse frsp = SendDataset(active, null, dataSet);
                    if (frsp != null)
                    {
                        active.WaitOnResponse();
                        bResponse = true;
                    }
                    return bResponse;
                }
            }
            finally
            {
                if (active != null)
                {
                    active.Release(true);
                }
                _aAssociateRequest.RemovePresentationContext(pcid);
            }

            return false;
        }

        private void ClearCache(object sender, ElapsedEventArgs e)
        {
            Logger.Info("Clearing Cache");
            _cacheManager.Clear();
        }

        private List<string> RetrieveItemsFromTheCache(IEnumerable<string> uniqueIds, List<DataSet> datasets)
        {
            var uniqueIdsNotCached = new List<string>();
            foreach (string uniqueId in uniqueIds)
            {
                if (_cacheManager.ContainsKey(uniqueId))
                {
                    datasets.AddRange(_cacheManager[uniqueId]);
                }
                else
                {
                    uniqueIdsNotCached.Add(uniqueId);
                }
            }
            return uniqueIdsNotCached;
        }

        private IActiveAssociation OpenAssociation()
        {
            IAssociation association = _associationFactory.NewRequestor(_hostName, _port);
            IPdu assocAC = association.Connect(_aAssociateRequest, ASSOCIATE_TIME_OUT);
            var associateRj = assocAC as AAssociateRJ;
            if (associateRj != null)
            {
                throw new DcmServiceException(associateRj.Reason(), associateRj.ReasonAsString());
            }
            _activeAssociation = _associationFactory.NewActiveAssociation(association, null);
            _activeAssociation.Timeout = ASSOCIATE_TIME_OUT;
            _activeAssociation.Start();
            return _activeAssociation;
        }

        private List<DataSet> RetrieveDatasetsFromServiceClassProvider(DataSet dataSet, string sopClassUniqueId)
        {
            int pcid = _presentationContextIdStart;
            _presentationContextIdStart += 2;
            var datasets = new List<DataSet>();
            IActiveAssociation activeAssociation = null;
            try
            {
                _aAssociateRequest.AddPresContext(_associationFactory.NewPresContext(pcid, sopClassUniqueId, DefinedTransferSyntaxes));
                activeAssociation = OpenAssociation();
                if (activeAssociation != null)
                {
                    datasets = ExecuteCFindDicomCommand(activeAssociation, dataSet, pcid);
                }
            }
            finally
            {
                if (activeAssociation != null)
                {
                    activeAssociation.Release(true);
                }
                _aAssociateRequest.RemovePresentationContext(pcid);
            }
            return datasets;
        }

        private List<DataSet> ExecuteCFindDicomCommand(IActiveAssociation active, DataSet dataSet, int pcid)
        {
            IAssociation association = active.Association;
            if (association.GetAcceptedPresContext(UIDs.StudyRootQueryRetrieveInformationModelFIND, TransferSyntaxUniqueId) == null)
            {
                Logger.Error(SOP_CLASS_UNIQUEID_NOT_SUPPORTED);
                return null;
            }
            IDicomCommand cFindDicomCommand = _dcmObjectFactory.NewCommand().InitCFindRQ(association.NextMsgID(), UIDs.StudyRootQueryRetrieveInformationModelFIND, Priority.HIGH);
            IDimse dimseRequest = _associationFactory.NewDimse(pcid, cFindDicomCommand, dataSet);
            string message = string.Format("{0} sending CFind request to {1} @ {2}:{3}", _aAssociateRequest.Name, _aAssociateRequest.ApplicationEntityTitle, _hostName, _port);
            Logger.Info(message);
            FutureDimseResponse dimseResponse = active.Invoke(dimseRequest);
            while (!dimseResponse.IsReady())
            {
                Thread.Sleep(0);
            }
            return dimseResponse.ListPending().Select(dimse => dimse.DataSet).ToList();
        }

        private FutureDimseResponse SendDataset(IActiveAssociation activeAssociation, DcmParser parser, DataSet dataSet)
        {
            String sopInstUniqueId = dataSet.GetString(Tags.SOPInstanceUniqueId);
            if (string.IsNullOrEmpty(sopInstUniqueId))
            {
                Logger.Error("SOP instance UniqueId is null or empty");
                return null;
            }
            String sopClassUniqueId = dataSet.GetString(Tags.SOPClassUniqueId);
            if (string.IsNullOrEmpty(sopClassUniqueId))
            {
                Logger.Error("SOP class UniqueId is null or empty");
                return null;
            }
            PresentationContext pc = null;
            IAssociation association = activeAssociation.Association;

            if (parser != null)
            {
                if (parser.DcmDecodeParam.encapsulated)
                {
                    String tsUniqueId = dataSet.FileMetaInfo.TransferSyntaxUniqueId;
                    if ((pc = association.GetAcceptedPresContext(sopClassUniqueId, tsUniqueId)) == null)
                    {
                        Logger.Error(SOP_CLASS_UNIQUEID_NOT_SUPPORTED);
                        return null;
                    }
                }
                else if (IsSopClassUniqueIdNotSupported(association, sopClassUniqueId, out pc))
                {
                    Logger.Error(SOP_CLASS_UNIQUEID_NOT_SUPPORTED);
                    return null;
                }

                DicomCommand cStoreRequest = _dcmObjectFactory.NewCommand().InitCStoreRQ(association.NextMsgID(), sopClassUniqueId, sopInstUniqueId, Priority.HIGH);
                return activeAssociation.Invoke(_associationFactory.NewDimse(pc.pcid(), cStoreRequest, new FileDataSource(parser, dataSet, new byte[2048])));
            }
            if ((dataSet.FileMetaInfo != null) && (dataSet.FileMetaInfo.TransferSyntaxUniqueId != null))
            {
                String tsUniqueId = dataSet.FileMetaInfo.TransferSyntaxUniqueId;
                if ((pc = association.GetAcceptedPresContext(sopClassUniqueId, tsUniqueId)) == null)
                {
                    Logger.Error(SOP_CLASS_UNIQUEID_NOT_SUPPORTED);
                    return null;
                }
            }
            else if (IsSopClassUniqueIdNotSupported(association, sopClassUniqueId, out pc))
            {
                Logger.Error(SOP_CLASS_UNIQUEID_NOT_SUPPORTED);
                return null;
            }

            DicomCommand cStoreRq = _dcmObjectFactory.NewCommand().InitCStoreRQ(association.NextMsgID(), sopClassUniqueId, sopInstUniqueId, Priority.HIGH);
            IDimse dimse = _associationFactory.NewDimse(pc.pcid(), cStoreRq, dataSet);
            return activeAssociation.Invoke(dimse);
        }

        private static bool IsSopClassUniqueIdNotSupported(IAssociation association, string sopClassUniqueId, out PresentationContext pc)
        {
            return (pc = association.GetAcceptedPresContext(sopClassUniqueId, UIDs.ImplicitVRLittleEndian)) == null;
        }

        private FileMetaInfo GenerateFileMetaInfo(string sopClassUniqueId)
        {
            var fileMetaInfo = new FileMetaInfo();
            fileMetaInfo.PutOB(Tags.FileMetaInformationVersion, new byte[] { 0, 1 });
            fileMetaInfo.PutUI(Tags.MediaStorageSOPClassUniqueId, sopClassUniqueId);
            fileMetaInfo.PutUI(Tags.TransferSyntaxUniqueId, TransferSyntaxUniqueId);
            fileMetaInfo.PutSH(Tags.ImplementationVersionName, "dicomSharp-SCU");
            return fileMetaInfo;
        }

        private static void Copy(Stream ins, Stream outs, int len, bool swap, byte[] buffer)
        {
            if (swap && (len & 1) != 0)
            {
                throw new DcmParseException("Illegal Length of OW Pixel Data: " + len);
            }
            if (buffer == null)
            {
                if (swap)
                {
                    int tmp;
                    for (int i = 0; i < len; ++i, ++i)
                    {
                        tmp = ins.ReadByte();
                        outs.WriteByte((Byte)ins.ReadByte());
                        outs.WriteByte((Byte)tmp);
                    }
                }
                else
                {
                    for (int i = 0; i < len; ++i)
                    {
                        outs.WriteByte((Byte)ins.ReadByte());
                    }
                }
            }
            else
            {
                byte tmp;
                int c, remain = len;
                while (remain > 0)
                {
                    c = ins.Read(buffer, 0, Math.Min(buffer.Length, remain));
                    if (swap)
                    {
                        if ((c & 1) != 0)
                        {
                            buffer[c++] = (byte)ins.ReadByte();
                        }
                        for (int i = 0; i < c; ++i, ++i)
                        {
                            tmp = buffer[i];
                            buffer[i] = buffer[i + 1];
                            buffer[i + 1] = tmp;
                        }
                    }
                    outs.Write(buffer, 0, c);
                    remain -= c;
                }
            }
        }

        #region Nested type: FileDataSource

        /// <summary>
        /// File Data source
        /// </summary>
        public sealed class FileDataSource : IDataSource
        {
            private readonly byte[] _buffer;
            private readonly DataSet _dataSet;
            private readonly DcmParser _parser;

            public FileDataSource(DcmParser parser, DataSet dataSet, byte[] buffer)
            {
                _parser = parser;
                _dataSet = dataSet;
                _buffer = buffer;
            }

            #region IDataSource Members

            public void WriteTo(Stream outs, String transferSyntaxUniqueId)
            {
                DcmEncodeParam netParam = DcmDecodeParam.ValueOf(transferSyntaxUniqueId);
                _dataSet.WriteDataSet(outs, netParam);
                if (_parser.ReadTag == Tags.PixelData)
                {
                    DcmDecodeParam fileParam = _parser.DcmDecodeParam;
                    _dataSet.WriteHeader(outs, netParam, _parser.ReadTag, _parser.ReadVR, _parser.ReadLength);
                    if (netParam.encapsulated)
                    {
                        _parser.ParseHeader();
                        while (_parser.ReadTag == Tags.Item)
                        {
                            _dataSet.WriteHeader(outs, netParam, _parser.ReadTag, _parser.ReadVR, _parser.ReadLength);
                            Copy(_parser.InputStream, outs, _parser.ReadLength, false, _buffer);
                        }
                        if (_parser.ReadTag != Tags.SeqDelimitationItem)
                        {
                            throw new DcmParseException("Unexpected Tag:" + Tags.ToHexString(_parser.ReadTag));
                        }
                        if (_parser.ReadLength != 0)
                        {
                            throw new DcmParseException("(fffe,e0dd), Length:" + _parser.ReadLength);
                        }
                        _dataSet.WriteHeader(outs, netParam, Tags.SeqDelimitationItem, VRs.NONE, 0);
                    }
                    else
                    {
                        bool swap = fileParam.byteOrder != netParam.byteOrder && _parser.ReadVR == VRs.OW;
                        Copy(_parser.InputStream, outs, _parser.ReadLength, swap, _buffer);
                    }
                    _dataSet.Clear();
                    _parser.ParseDataset(fileParam, 0);
                    _dataSet.WriteDataSet(outs, netParam);
                }
            }

            #endregion
        }

        #endregion
    }
}