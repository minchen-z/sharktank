import * as React from 'react';
import { Route, Redirect } from 'react-router-dom';
import { RouteComponentProps } from 'react-router';
import { ApiHandlers } from '../handlers';
import { Auth } from '../auth';

interface LogoutState {
    redirect: boolean;
}

export class Logout extends React.Component<RouteComponentProps<{}>, LogoutState> {

    constructor() {
        super();

        this.state = { redirect: false };

        Auth.clearLoggedInCache();

        fetch(ApiHandlers.Url + 'Api/Auth/Logout', {
            credentials: 'include'
        })
            .then(response => ApiHandlers.handleErrors(response))
            .then(data => this.setState({ redirect: true }))
            .catch(error => ApiHandlers.handleCatch(error));
    }

    public render() {
        return this.state.redirect ? <Redirect to="/Console/Login" push /> : null;
    }
}
