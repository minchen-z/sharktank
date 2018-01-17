import * as React from 'react';
import { RouteComponentProps } from 'react-router';
import { LoadingButton } from './LoadingButton';

interface AppsState {
    apps: App[];
    loading: boolean;
}

export class Apps extends React.Component<RouteComponentProps<{}>, AppsState> {
    constructor() {
        super();
        this.state = { apps: [], loading: true };

        fetch('api/apps')
            .then(response => response.json() as Promise<App[]>)
            .then(data => {
                this.setState({ apps: data, loading: false });
            });
    }

    public render() {
        let contents = this.state.loading ? <p><em>Loading...</em></p> : this.renderTable(this.state.apps);

        return <div>
            <h1>Your Apps</h1>

            {contents}

        </div>;
    }

    private renderTable(apps: App[]) {
        return <div className="table-responsive">
            <table className="table table-striped">
                <thead>
                    <tr>
                        <th>App Id</th>
                        <th>Access Key</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    {apps.map(app =>
                        <tr key={app.appId}>
                            <td>{app.appId}</td>
                            <td>{app.accessKey}</td>
                            <td><LoadingButton>Delete</LoadingButton></td>
                        </tr>
                    )}
                </tbody>
            </table>
        </div>;
    }

    deleteApp(appId: string) {

        const formData = new FormData();

        formData.append('id', appId);

        fetch('api/apps', { method: 'DELETE', body: formData })
            .then(data => {
                //this.setState({ apps: data, loading: false });
            });
    }
}

interface App {
    appId: string;
    accessKey: string;
}
